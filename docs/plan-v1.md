# Talky — Kurumsal On-Prem İletişim Platformu: Uygulama Planı (v1)

## 1. Bağlam

Talky, Microsoft Teams / Skype for Business tarzında, tamamen kurum içinde (on-prem) çalışacak bir kurumsal iletişim uygulamasıdır. İki üst öncelik: **çok hızlı** ve **hatasız**. Üçüncü, sonradan eklenen ve tasarımı şekillendiren kısıt: **mümkün olduğunca az ayrı ürün** (yönetilebilirlik).

Netleşen gereksinimler:

| Konu | Karar |
|---|---|
| v1 kapsamı | Mesajlaşma (1:1, grup, kanal, thread, reaksiyon, okundu, yazıyor, presence, arama, düzenle/sil), 1:1 sesli/görüntülü arama, grup toplantısı + ekran paylaşımı, dosya paylaşımı + önizleme |
| İstemciler | Web (React) + Masaüstü (Windows öncelikli, Tauri 2) + Mobil (iOS/Android, React Native) |
| Backend | **.NET 10 (C#)** — AD/Kerberos/çok-forest entegrasyonu için Go'ya tercih edildi |
| Sunucu OS | **Tamamen Windows Server** (2022/2025) |
| Ölçek | **10.000 – 50.000 kullanıcı**, yatay ölçeklenebilir, tek sunucu kurulumu da mümkün |
| Kimlik | Çok-forest Active Directory (Kerberos/IWA + LDAP + ADFS OIDC; SAML v1.1) |
| Forestlar arası | (a) Tek kurulumda çoklu forest kimliği → **v1**; (b) ayrı Talky kurulumları arası **federasyon → v1.1**, v1 buna engel olmayacak şekilde tasarlanır |
| Yönetilebilirlik | Üretimde yalnızca **3 ürün**: `talky.exe`, SQL Server, LiveKit. Geri kalanı Windows'un kendi altyapısı |

Hedef: `docker`, NATS, Redis, MinIO, coturn, Caddy, Prometheus/Grafana, GlitchTip gibi ayrı ürünler **yok**. İşlevleri ya `talky.exe` içine ya SQL Server'a ya da Windows'un yerleşik özelliklerine taşınır.

---

## 2. Üretim Ayak İzi (ne kurulacak)

```
                 ┌──────────── Kurumsal LB (F5/NetScaler) veya DNS RR ────────────┐
                 │                         https://talky.corp                      │
                 ▼                                                                 ▼
  ┌──────────────────────────┐   gRPC mesh   ┌──────────────────────────┐
  │ talky.exe  (API node 1)  │◄─────────────►│ talky.exe  (API node N)  │  3–6 × Windows Server
  │ Kestrel: HTTPS/WS/REST   │               │ + worker / search rolleri│  8 vCPU / 16 GB (search: 64 GB, NVMe)
  │ + YARP → LiveKit /rtc    │               │                          │
  └───────────┬──────────────┘               └───────────┬──────────────┘
              │                                          │
     ┌────────▼──────────┐   ┌──────────────────┐   ┌────▼─────────────────┐
     │ SQL Server 2025   │   │ SMB/DFS dosya    │   │ LiveKit (Windows svc)│  N bağımsız medya node
     │ Always On AG      │   │ paylaşımı        │   │ gömülü TURN, UDP 7882│  16 vCPU / 32 GB / 10GbE
     └───────────────────┘   └──────────────────┘   └──────────────────────┘

  DMZ: talky.exe --role push-relay  (yalnız APNs/FCM'ye giden 443)
```

Bileşen bazında "ne yerine ne":

| İhtiyaç | Klasik çözüm | Talky'de |
|---|---|---|
| Node'lar arası olay yayını | NATS / Redis | `talky.exe` içinde **gRPC tam-mesh küme bus'ı**; node kaydı SQL'de |
| Arka plan iş kuyruğu | JetStream / Hangfire+Redis | SQL Server `jobs` tablosu (`READPAST` claim) |
| Dosya depolama | MinIO / S3 | `IBlobStore` → **SMB/DFS paylaşımı** (yüklemeler API üzerinden proxy) |
| Tam metin arama | Elasticsearch / OpenSearch / Meilisearch | `talky.exe --role search`: **gömülü Lucene.NET** indeksi (yeni ürün yok; SQL kaynak-gerçek, indeks yeniden inşa edilebilir) |
| TLS / reverse proxy | Caddy / nginx | **Kestrel** doğrudan HTTPS; LiveKit sinyalleşmesi **YARP kütüphanesi** ile aynı binary'den proxy |
| TURN | coturn | **LiveKit gömülü TURN** |
| LiveKit çoklu node | Redis | Backend odayı en az yüklü LiveKit node'una **kendisi atar**; node'lar bağımsız |
| Metrik / log / hata izleme | Prometheus+Grafana+Loki+Sentry | **Yerleşik Admin Paneli** (SQL'de 1-dk agregalar) + Serilog dosya + Windows Event Log; isteğe bağlı OTLP/Prometheus export |
| Virüs tarama | ClamAV | **Windows Defender AMSI** (`AmsiScanBuffer`) |
| Masaüstü güncelleme sunucusu | ayrı servis | `talky.exe` `/updates` endpoint'i (minisign imzalı) |
| SSO sağlayıcı | Keycloak | Kurumun **mevcut AD/ADFS**'i; Talky yalnızca RP/istemci |

---

## 3. Backend Mimarisi (.NET 10)

### 3.1 Topoloji
- **Modüler monolit, tek binary `talky.exe`**, Windows servisi (`UseWindowsService`), **gMSA** hesabıyla çalışır (parola yok, Kerberos SPN gMSA üzerinde).
- Roller: `--role all|api|worker|search|push-relay`. `all` tek sunucu kurulumu; büyük kurulumda 3–6 `api` + 1–2 `worker` + 2 `search`; DMZ'de `push-relay`.
- Durumsuz node'lar: kalıcı durum yalnızca SQL Server + dosya paylaşımı. Node-yerel tek durum WS bağlantıları.
- Zamanlanmış işler (AD sync, retention, temizlik) için SQL `sp_getapplock` ile lider kilidi → tek node çalıştırır.

### 3.2 Küme bus'ı (NATS yerine)
- `cluster_nodes(node_id, host, grpc_port, version, heartbeat_at)`; 5 sn heartbeat, 15 sn sonra ölü sayılır.
- Her API node diğer tüm canlı node'lara **gRPC bidirectional stream** açar (N≤6 → tam mesh). Yayın: `Envelope{subject, payload}`; alıcı node yerel `conv_id → sessions` indeksine göre dağıtır.
- **Doğruluk bus'a dayanmaz**: her olay önce SQL'e commit edilir (`seq` atanır), bus yalnızca "hemen ilet" içindir. Bir peer bağlantısı >2 sn koparsa node kendi oturumlarına `resync_required` gönderir; istemci `/sync` + gap-fill ile toparlar. Kontrollü kapanışta node önce "ayrılıyorum" der (drain), resync tetiklenmez.
- Efemer olaylar (typing, presence) best-effort; kuyruk dolarsa önce onlar düşer.
- Presence: her node tam presence haritasını tutar (50k giriş ≈ birkaç MB), yalnızca geçişlerde yayınlanır; yeni node katılınca snapshot alır.

### 3.3 Gerçek zamanlı protokol (WebSocket)
- Endpoint `wss://talky.corp/ws?v=1`; kimlik ilk `hello` çerçevesinde (query string'de asla).
- Zarf **JSON** (proto3 JSON kodlaması; ileride binary'ye geçiş bayrak değişimi). Sözleşme tek kaynak: `api/proto/talky/v1/*.proto` → C# (`Google.Protobuf`) ve TS (`buf` + `@bufbuild/protobuf`) üretimi; CI drift'te kırılır.
- **Sıralama**: konuşma başına `seq bigint`, insert transaction'ında `UPDATE conversations SET last_seq += 1 OUTPUT` ile atanır (satır kilidi = toplam sıra, koordinatör yok).
- **İdempotency**: istemci `client_msg_id` (UUIDv7) üretir; unique index `(sender_id, client_msg_id)`; tekrar gönderim orijinal ack'i döner.
- **Teslimat**: sunucu→istemci at-least-once; istemci `(conv_id, seq)` ile dedupe. Sunucu ack beklemez, resume'a güvenir.
- **Resume**: yeniden bağlanan istemci açık konuşmalar için `hello{since:{cid:seq}}` gönderir, geri kalan için `GET /sync` (konuşma listesi + `last_seq`, `last_read_seq`). >500 eksik → `resync_required`, HTTP ile sayfalama. Kullanıcı başına olay günlüğü **yok** (5k üyeli kanalda yazma çoğalmasını önler).
- **Backpressure**: bağlantı başına sınırlı `Channel<T>` (256); dolarsa `resync_required` gönderilip kuyruk boşaltılır, hub bloklanmaz. Yazıcı 10 ms'de bir olayları tek çerçevede birleştirir.
- **Heartbeat**: sunucu 25 sn ping, 60 sn pong yoksa kapat; tek timer-wheel (bağlantı başına timer yok). İstemci üstel backoff + jitter (0.5–30 sn).
- Kütüphaneler: `Microsoft.AspNetCore.WebSockets` (raw), `System.Threading.Channels`, `System.Text.Json` source-generated. SignalR yakın alternatif; seq/resume kontrolü için raw WS seçildi.

### 3.4 HTTP API
- Minimal API, `/api/v1/...`, yerleşik `Microsoft.AspNetCore.OpenApi` → `openapi-typescript` (istemci tipleri).
- HTTP: giriş/SSO, `/sync`, geçmiş (`GET /conversations/{id}/messages?before_seq=&limit=50`), thread, arama, yükleme, admin, arama/toplantı token'ı, LiveKit webhook.
- WS: `msg.send/edit/delete`, `reaction.*`, `read.mark`, `typing`, `presence.sub`, `conv.focus`, `call.*` ve tüm sunucu olayları.
- Sürümleme: path `v1`, yalnızca ekleme; `hello.proto=1`.

### 3.5 Veri modeli (SQL Server 2025)
- **ID'ler UUIDv7** (`Guid.CreateVersion7()`), mesajlar ayrıca `(conversation_id, seq)`.
- Tablolar (özet): `identity_sources`, `users` (`source_id`, `object_guid`, `sid`, `upn`, `home_domain`, …), `teams`, `team_members`, `conversations` (`type dm|group|channel`, `last_seq`, `home_domain`), `dm_pairs`, `conversation_members` (`last_read_seq`, `mention_count`, `muted`, `notify_level`), **`messages`** (clustered PK `(conversation_id, seq)`, unique `(sender_id, client_msg_id)`, `parent_id` thread kökü, `body`, `metadata json`, `deleted_at`), `thread_meta`, `reactions`, `attachments`, `calls`, `call_participants`, `devices`, `sessions`, `jobs`, `cluster_nodes`, `metrics_1m`, `client_errors`, `audit_log`, `federation_peers` (v1.1 için şema yeri).
- Sıcak sorgular: geçmiş = PK aralık taraması; **okunmamış = `last_seq − last_read_seq`** (sayım yok); konuşma listesi = `conversation_members(user_id)` join.
- Hacim (50k kullanıcı × ~50 mesaj/gün ≈ 900M satır/yıl): page compression; bölümleme v1'de yok (bölümleme `created_at`'i anahtara sokar ve sıcak sorguyu 24 bölüme yayar). Retention silme clustered anahtar boyunca `DELETE TOP(5000) WHERE conversation_id=@c AND seq<@keep` partileriyle, mesai dışı.
- Erişim: **EF Core 10** (model + migrations + CRUD) ve 5–6 sıcak yol için ham SQL (`SqlQuery`). Tek kütüphane, Dapper yok.

#### 3.5.1 Büyük kurumda geçmiş neden "çile"ye dönmez
- "Bu konuşmanın son 50 mesajı" = clustered `(conversation_id, seq)` üzerinde aralık taraması → tablo 100M de olsa 3B de olsa **3–5 sayfa okuma**; maliyet tablo boyutundan bağımsız. Sonraki sayfalar keyset (`seq < @before`), asla `OFFSET`.
- Sayım yok: okunmamış = `last_seq − last_read_seq`; thread sayıları `thread_meta`'da denormalize; konuşma listesi `last_message_at` denormalize. Mesaj tablosunda hiçbir `COUNT(*)`/`GROUP BY` yolu yok.
- Yazma yükü: 900M/yıl ≈ ortalama 30 insert/sn, tepe ~500/sn → SQL Server için düşük. Log/tempdb ayrı NVMe, `READ_COMMITTED_SNAPSHOT` açık (okuyucular yazarları bloklamaz).
- İstemci tarafı: son 200 mesaj/konuşma yerel cache'te; kanal geçişi çoğunlukla sunucuya gitmez.
- **Kanıt zorunlu**: M2 çıkış kriterine 500M satırlık sentetik `messages` tablosunda geçmiş p99 <50 ms ve `/sync` p99 <100 ms eklendi (§9).

### 3.5.2 Arama: gömülü Lucene.NET (`--role search`)
- **Neden SQL FTS değil**: `CONTAINSTABLE` önce tüm konuşmalarda eşleşir, yetki filtresi sonra gelir → yaygın kelimede yüz milyonlarca aday; yazım toleransı yok; Türkçe ek çözümü zayıf; sıralama kalitesi düşük. 10k kullanıcı altında kabul edilebilir, 50k'da global arama çileye döner. Elasticsearch/OpenSearch (JVM küme) yönetilebilirlik kısıtına aykırı; Meilisearch ~100M belge üstünde uygun değil.
- **Tasarım**: her `search` node kendi Lucene.NET 4.8 indeksini yerel NVMe'de tutar ve **SQL'deki olay akışından bağımsız olarak inşa eder** (doc id = message_id, idempotent) → node'lar birebir aynı içeriğe sahip, dosya replikasyonu yok; sorgu herhangi sağlıklı node'a mesh üzerinden gider. 2 node = HA. Indeks bir cache'tir: bozulursa/versiyon değişirse SQL'den yeniden inşa (`talky search rebuild`, 1B mesaj için saatler, standby'da çalışır).
- **İndeks içeriği**: `message_id, conversation_id, sender_id, created_at, has_attachment, body (analyzed), body_stored` (snippet için). Analizör: `TurkishAnalyzer` (İ/ı doğru küçük harf) + İngilizce; `FuzzyQuery` (1 edit) ile yazım toleransı; `TermInSetQuery(conversation_id ∈ kullanıcının konuşmaları)` ile **ACL indeks içinde** filtrelenir; filtreler `from:`, `in:`, `before:/after:`, `has:file`.
- **Gecikme**: `msg.new/edit/delete` → `jobs` → indeks ≤2 sn. Silinen mesaj indeksten anında düşer.
- **Boyut**: ~60–80 GB/yıl (gövde saklanarak); node 16 vCPU / 64 GB / 1 TB NVMe. mmap ile sıcak sorgular <100 ms.
- Arayüz `ISearchIndex` (tek implementasyon; SQL FTS alternatifi yazılmaz, test matrisi ikilenmez). Gelecek: SQL Server 2025 vektör tipi ile semantik arama v2.

### 3.6 Kimlik: çok-forest Active Directory
- **Kaynaklar**: `identity_sources` → forest adı, GC sunucuları (`System.DirectoryServices.ActiveDirectory.Forest` ile otomatik DC locator), bağlanma kimliği, alan adı sonekleri, grup→takım eşlemeleri, sync aralığı.
- **Senkron**: 15 dk'da bir `System.DirectoryServices.Protocols` ile paged search (GC 3269/LDAPS), anahtar `objectGUID`, `objectSid` saklanır; kaybolan kullanıcı devre dışı; iç içe grup çözümü `LDAP_MATCHING_RULE_IN_CHAIN` (1.2.840.113556.1.4.1941).
- **Kimlik doğrulama yolları** (tek oturum modeline iner):
  1. **Kerberos / Negotiate** (`Microsoft.AspNetCore.Authentication.Negotiate`, Windows'ta SSPI): domain'e bağlı makinelerden şifresiz giriş. **Forest trust** varsa diğer forestların kullanıcıları da otomatik doğrulanır. SPN `HTTP/talky.corp` gMSA'da; Edge/WebView2 için GPO `AuthServerAllowlist`.
  2. **OIDC** (`...Authentication.OpenIdConnect`) → kurumun ADFS'i (trust'sız forestlar için her forest'ın kendi ADFS'i tanımlanabilir).
  3. **LDAP bind** (kullanıcı adı/parola formu, `user@domain` ile kaynağa yönlendirme) — trust yok ve ADFS yoksa.
  4. SAML (`Sustainsys.Saml2`) → v1.1.
- **Oturum**: opaque token (32 bayt, SHA-256 hash `sessions`'ta), web'de httpOnly cookie, Tauri/RN'de Bearer; 30 gün kayan / 90 gün mutlak; anında iptal (node-yerel 60 sn LRU + bus ile invalidation).
- **RBAC**: sabit roller `org_admin`, `team_owner`, `team_member`, `conv_owner`, `conv_member`, `guest`; tek `Authz` sınıfından geçer.

### 3.7 Federasyon (v1.1) — v1'de alınacak önlemler
- Her kullanıcı ve konuşmada `home_domain`; adresler `user@domain` biçiminde global-benzersiz.
- Konuşmanın **ev sunucusu** `seq` otoritesidir; uzak sunucu kendi kullanıcıları için ayna tutar; uzak kullanıcının gönderimi ev sunucuya HTTPS ile gider.
- Sunucular arası protokol: gRPC over HTTPS + **mTLS**, allowlist (`federation_peers`), keşif statik veya DNS SRV `_talky._tcp`.
- Aramalar: ev sunucunun LiveKit'i token verir; TURN/TLS ile dış erişim. Dosyalar: imzalı federe URL.
- v1'de: `home_domain` sütunları, `IEventSink` soyutlaması (local/remote), olay şeması imzalanabilir, yetki kontrolleri `(domain, user)` üzerinden, istemcide "uzak kullanıcı stub'ı" desteği.

### 3.8 Dosyalar
- `IBlobStore`: v1 `FileSystemBlobStore` (SMB/DFS paylaşımı, anahtar `org/{cid}/{uuid}/{name}`); S3 implementasyonu arayüz olarak hazır.
- Akış: `POST /uploads` (chunk'lı, resumable, 8 MB parça) → API paylaşıma stream eder → `complete` → `jobs`: AMSI tarama, önizleme (`SixLabors.ImageSharp` görsel, `PDFtoImage` PDF ilk sayfa, blurhash). Limit 100 MB (yapılandırılabilir).
- İndirme: kısa ömürlü imzalı URL `/files/{id}?sig=`, üyelik kontrolü. Retention mesajla birlikte; gece yarısı sahipsiz dosya temizliği.

### 3.9 Medya (LiveKit)
- **Neden LiveKit**: Go/Pion SFU, resmî web + React Native SDK'ları, simulcast/dynacast, `lk load-test`, Apache-2.0; Windows için resmî exe var. Jitsi (Java, zayıf RN), mediasoup (RN SDK yok, sinyalleşmeyi biz yazarız), özel SFU (yıllar) elendi.
- **1:1 aramalar da SFU üzerinden** (tek kod yolu, NAT'ta yalnız istemci→sunucu erişimi yeter).
- **Windows'ta**: `livekit-server.exe` WinSW ile Windows servisi; `rtc.udp_port 7882` (tek mux port), `rtc.tcp_port 7881` (ICE-TCP), gömülü TURN (`udp 3478`, `tls 5349`; ayrı IP ile 443). Sinyalleşme `wss://talky.corp/media/{node}/rtc` → YARP → `http://node:7880`. **M0 spike**: Windows Server'da `lk load-test` (100 abone, 8 yayıncı); CPU >60 % veya kayıp >1 % ise **Hyper-V üzerinde Ubuntu VM appliance** yedek plana geçilir (aynı config).
- **Çoklu node, Redis'siz**: `talky` oda açarken node'ların `/health` + katılımcı sayısına göre en az yüklüyü seçer; oda tek node'da yaşar. Node ölürse istemciler yeni token'la başka node'a yeniden katılır (5–8 sn).
- **Entegrasyon**: JWT (`Microsoft.IdentityModel.JsonWebTokens`, TTL 10 dk), RoomService Twirp HTTP çağrıları için ince in-house istemci; webhook `POST /internal/livekit/webhook` (JWT doğrulama, idempotent durum geçişleri). Arama durum makinesi `ringing → active → ended{completed|missed|declined|cancelled|failed}`; `call.invite/ringing/accept/decline/cancel/timeout(30 sn)/ended` chat WS'i üzerinden; `room_finished` → "Arama başladı 12:03 · 14 dk" sistem mesajı. 30 sn'de bir hayalet katılımcı mutabakatı.
- **Kalite**: VP8 (her platformda çözülür), simulcast h720/h360/h180, mobil h540; `dynacast`, `adaptiveStream`; Opus speech 24 kbps + DTX + RED; ekran `detail` 1080p15 + 720p5 katmanı. DSCP AF41 GPO ile.
- **Kapasite (50k)**: tepe ≈ 1.500 1:1 + 150 toplantı → ~8 Gbps çıkış → 4–6 medya node (16 vCPU/32 GB/10 GbE); 10k için 2 node yeter. Formül: yayıncı başına ~1.5 Mbps, abone başına katman bant genişliği.
- **Güvenlik**: DTLS-SRTP zorunlu, `keys` yalnız backend ağında, E2EE v1'de kapalı (kayıt/transkripsiyon yolunu kapatır; v2 "gizli toplantı" bayrağı). Lobi backend tarafında (token verilmez), webinar modunda `canPublish:false`.
- **Gelecek**: Egress kayıt (ayrı node, MinIO yerine paylaşım), Agents ile transkripsiyon, SIP gateway — oda adı/metadata ve identity=userId tasarımı bunları engellemez.

### 3.10 Bildirimler
- **Push relay**: `talky.exe --role push-relay` DMZ'de; yalnız `api.push.apple.com:443` ve `fcm.googleapis.com:443` çıkışı. İçeriksiz payload `{cid, msgId}`; istemci uyanınca çeker. Web push VAPID. Aktif WS oturumu varken push yok; masaüstünde aktifken mobil push 10 sn geciktirilir.
- iOS gelen arama: PushKit → APNs → internet çıkışı şart; çıkış reddedilirse iOS yalnız ön plandayken çalar (yazılı kabul alınır). Android: WS ön plan servisi ile çıkışsız çalabilir.
- Sayım kuralları: okunmamış = `seq > lastReadSeq && sender != me`; düzenleme/reaksiyon/kendi mesajı saymaz; thread yanıtı kanalı değil Activity'yi artırır; badge = DM + grup + kanal mention'ları. Okundu işareti yalnız pencere odaklı + konuşma açık + satır ≥500 ms görünür + 1 sn debounce.

---

## 4. İstemci Mimarisi

### 4.1 Monorepo
- `/server` (.NET sln) ve `/clients` (pnpm workspace + Turborepo) tek repoda: proto/OpenAPI, C# ve TS aynı PR'da değişir.
- `clients/packages/protocol` (yalnız üretilmiş tipler, commit'li, drift'te CI kırılır), `core` (DOM/RN import'suz: `ws/` resume, `api/`, `store/` Zustand vanilla, `sync/` olay reducer + gap + backfill, `outbox/`, `storage/` arayüz, `auth/`, `platform/` arayüz), `hooks`, `tokens`, `i18n` (tr kaynak, en takip), `config`, `test-utils` (fixture oynatan sahte WS sunucusu).
- `apps/web` (Vite), `apps/desktop` (`src-tauri/` — frontend `apps/web`'in `PLATFORM=desktop` build'i), `apps/mobile` (Expo).
- **Paylaşım stratejisi**: yalnız mantık paylaşılır (core+hooks+tokens+i18n); UI web/masaüstü %100 ortak, mobil ayrı. Evrensel UI (Tamagui/RNW) liste performansı ve build kırılganlığı nedeniyle reddedildi.

### 4.2 Web
- React 19 + Vite + TS strict + React Compiler; TanStack Router (tipli search params); Zustand dilimleri (`session`, `users`, `conversations`, `timeline(convId)` LRU 50, `ephemeral`, `ui`).
- **Sync motoru** (TanStack Query yerine): tek yazma yolu `applyEvents()`; WS olayları ve REST sayfaları aynı olaya dönüşür; timeline = bitişik seq aralıkları, delik = `gap` işareti → backfill. Query yalnız arama/ayarlar/admin için.
- Liste: **react-virtuoso** (`firstItemIndex` ile zıplamasız prepend, `followOutput`, `rangeChanged` okundu). Editör: **TipTap** (mention/emoji/markdown kısayolları). Stil: Tailwind v4 + Radix/shadcn. İlk bundle ≤250 KB gz; TipTap, livekit-client, pdf.js, emoji verisi lazy.
- Kalıcılık: Dexie 4 Web Worker içinde (comlink); son 200 mesaj/konuşma (500 konuşma, ~50 MB), tam kullanıcı dizini, taslaklar, outbox. **Outbox**: `clientMsgId` compose'da; optimistik render'dan ÖNCE persist; 3 hatadan sonra "gönderilemedi, yeniden dene" ama asla düşürülmez.
- Çoklu sekme: Web Locks lider seçimi, tek WS, BroadcastChannel.
- Giriş: intranet bölgesinde `fetch('/auth/negotiate')` ile sessiz Kerberos; olmazsa OIDC (PKCE, `oidc-client-ts`) veya LDAP formu.

### 4.3 Masaüstü (Tauri 2, Windows öncelikli)
- WebView2 → Negotiate desteği ile domain makinelerinde **sıfır tık SSO**; `getDisplayMedia` WebView2'de çalışır (Chromium picker). macOS/Linux ikincil; macOS ekran paylaşımı için Rust yakalama plugin'i spike'ı C2'de, başarısızsa Electron kararı orada verilir.
- Rust kabuk: aksiyonlu bildirim (Windows'ta `windows-rs` Toast), tray, autostart, badge, global kısayol, `talky://` deep link, single-instance, `keyring` (Credential Manager) ile refresh token, `updater` (minisign, `talky.exe /updates`), `window-state`, idle algılama.
- Paketleme: **MSI (WiX)**, `msiexec /i Talky.msi /qn SERVER_URL=https://talky.corp`; GPO/Intune sessiz kurulum; OV/EV kod imzası (SmartScreen); WebView2 `offlineInstaller`, air-gapped için `fixedRuntime`. Proxy/PAC ve kurum CA'sı WebView2 ile sistemden gelir.

### 4.4 Mobil (React Native / Expo)
- Expo SDK ≥54, dev client, `expo prebuild`; build Fastlane ile şirket içi Mac runner (EAS yok). JS OTA v1'de yok, dağıtım MDM ile (Apple Business Manager Custom Apps / Managed Google Play; Managed App Config ile `serverUrl`).
- Expo Router; `@livekit/react-native` + `react-native-callkeep` + `react-native-voip-push-notification`; Android ön plan servisi (`microphone|camera`); `op-sqlite` (FTS5) önbellek; `expo-secure-store` + biyometrik kilit (5 dk).
- Arka planda kalıcı WS yok; `AppState active` → reconnect + resume.
- Giriş: `expo-auth-session` (ASWebAuthenticationSession/Custom Tabs) → ADFS OIDC; veya LDAP formu.

### 4.5 Yerleşik Admin Paneli (web `/admin`)
- Kimlik kaynakları + sync durumu, kullanıcı/takım yönetimi, politikalar (retention, DND, dosya limiti, federasyon allowlist), **node sağlığı ve canlı metrikler** (WS sayısı, fan-out p99, SQL bekleme, LiveKit oda/katılımcı, TURN kullanımı), istemci hata raporları, arama kalitesi (RTT/jitter/kayıp), audit log, oturum iptali.

---

## 5. Kurulum, İşletim, Güvenlik
- **Kurulum**: `Talky.Server.msi` (WiX) → `talky.exe` Windows servisi (gMSA), `C:\ProgramData\Talky\` config+log; `Install-Talky.ps1`: SQL şema (`talky migrate` EF bundle), sertifika bağlama (Windows cert store), güvenlik duvarı kuralları, SPN kontrolü; `talky doctor`: AD/GC erişimi, SQL, paylaşım, LiveKit, saat senkronu, sertifika doğrulaması.
- **Config**: `appsettings.Production.json` + ortam değişkenleri; sır yok (gMSA + Windows cert store); zorunlu değerler yoksa başlangıçta fail-fast.
- **HA**: LB `/readyz` (SQL, paylaşım, ≥1 LiveKit); SQL Always On AG (2 senkron replika); paylaşım WSFC veya DFS-R; medya N node.
- **Sıfır kesinti güncelleme**: servis durdurulurken node LB'de unready → WS istemcilere `reconnect{jitter 0–10 sn}` → 30 sn drain; MSI upgrade node node; EF migration "expand/contract" kuralı; bir minor sürüm skew'i tolere edilir.
- **Loglama/izleme**: Serilog rolling dosya (JSON) + Warning↑ Event Log; `System.Diagnostics.Metrics` → SQL `metrics_1m` (admin panel) ve isteğe bağlı OTLP/Prometheus `/metrics` (kurumun mevcut SCOM/Zabbix/Prometheus'u için).
- **Yedekleme**: SQL Server native backup + AG; paylaşım VSS/DFS-R; çeyrek yılda bir geri yükleme tatbikatı sürüm kontrol listesinde.
- **Güvenlik**: TLS 1.2+/HTTP/2 Kestrel, HSTS, CSP; sertifikalar kurum CA'sından (dış kullanıcılar için `media/turn` host'larında public CA); rate limit (`Microsoft.AspNetCore.RateLimiting`); audit log değişmez tablo; AMSI tarama; RBAC tek noktadan.

---

## 6. Performans Bütçesi (LAN)

| Hedef | Değer | Tasarım kaldıracı |
|---|---|---|
| WS bağlantı / 8 vCPU node | 15k | bağlantı başına ~40 KB, tek timer-wheel, `Channel<T>` |
| send → ack p99 | <30 ms | tek transaction, compiled query, pool |
| fan-out p99 | <150 ms | gRPC mesh (<1 ms), yerel indeks, 10 ms birleştirme |
| geçmiş sayfa p99 | <50 ms | clustered PK aralık taraması, gönderen bilgisi cache'ten |
| `/sync` p99 | <100 ms | tek sorgu, okunmamış aritmetik |
| arama p99 (1B mesaj, global) | <100 ms | Lucene mmap, ACL indeks içinde, limit 50 |
| arama indeks gecikmesi | ≤2 s | `jobs` tabanlı indexer |
| Web soğuk TTI (sıcak cache) | ≤1.5 s | IndexedDB'den liste ≤500 ms, WS'ten önce |
| Masaüstü açılış → boyanmış UI | ≤1.0 s | pencere ilk paint'e kadar gizli |
| Kanal geçişi | ≤50 ms bellekten, ≤120 ms diskten | LRU 50 timeline |
| 100k mesajlı kanal | 60 fps | ≤60 DOM satırı, worker'da markdown/arama |
| Masaüstü RSS | ≤300 MB mesajlaşma, ≤600 MB toplantı | lazy chunk'lar, ≤2k mesaj/timeline bellekte |
| Medya | join başarı ≥99.5 %, ilk medya p95 <2 s | simulcast, dynacast, TURN/TLS fallback |

---

## 7. Kalite ve Test Stratejisi ("hatasız")
- **Sunucu**: xUnit birim (servis katmanı, Authz, WS hub); entegrasyon SQL Server Developer/LocalDB üzerinde (her sıcak sorgu için test, migration up/down); **sözleşme testleri** `api/fixtures/*.json` (connect, resume, gap, dup, reorder, revoke) hem .NET hem Vitest tarafından çalıştırılır; property-based (`FsCheck`): 100 eşzamanlı gönderende `seq` monoton ve boşluksuz, resume tam boşluğu oynatır.
- **Yük** (gecelik): `tools/wsload` (.NET) 15k bağlantı, 1k üyeli kanala 200 msg/s → p99 <150 ms, 0 kayıp; k6: geçmiş 500 rps p99 <50 ms; `lk load-test` 30 paralel oda.
- **Kaos** (gecelik): API node kill → ≤5 sn'de tüm istemciler geri, seq denetimiyle 0 eksik; SQL AG failover → ≤10 sn; LiveKit node kill → ≤10 sn yeniden katılım; 24 saat soak (heap/handle sızıntı assert'i).
- **İstemci**: TS `strict` + `noUncheckedIndexedAccess`; ESLint strict-type-checked + import sınırları (core'da react-dom/RN yasak); Vitest + fast-check (rastgele sıra/boşluk/dup → değişmezler); Testing Library; Playwright (web + Tauri `tauri-driver`, sahte medya cihazlarıyla arama E2E: `framesDecoded>0` ≤5 s); Maestro (mobil); Lighthouse CI + 100k sentetik kanal perf testi (%10 gerileme → fail); `size-limit`.
- **Ağ bozulma matrisi**: `%2/5/10/20` kayıp, 80 ms gecikme, UDP kapalı (TURN/TLS), 10 sn WS kesintisi, Wi-Fi→LTE, uyku/uyanma.
- **AD laboratuvarı**: Hyper-V'de 2 forest (Windows Server eval), forest trust, trust'sız 3. forest, ADFS; çok-forest giriş ve sync testleri burada.
- **CI**: lint → birim → entegrasyon → sözleşme → MSI/istemci build → imza; gecelik yük+kaos; sürüm için gecelik yeşil şart. Rollout halkaları: IT canary → %10 → herkes.
- Hedefler: mesaj kaybı 0; WS resume başarı ≥99.9 %; crash-free oturum ≥99.8 % masaüstü/web, ≥99.5 % mobil.

---

## 8. Repo Yapısı

```
talky/
  server/
    Talky.sln
    src/Talky.Server/        host: Kestrel, roller, Windows servisi, YARP rotaları
    src/Talky.Core/          domain, servisler, Authz, olaylar
    src/Talky.Data/          EF Core model, migrations, sıcak SQL
    src/Talky.Realtime/      WS hub, protokol, küme bus'ı (gRPC), presence
    src/Talky.Identity/      AD sync, Negotiate, OIDC, LDAP bind, oturumlar
    src/Talky.Media/         LiveKit istemcisi, arama durum makinesi, webhook
    src/Talky.Files/         IBlobStore, chunk upload, AMSI, önizleme
    src/Talky.Search/        Lucene.NET indexer + sorgu, Türkçe analizör, rebuild
    src/Talky.Push/          APNs/FCM/WebPush, push-relay rolü
    src/Talky.Federation/    v1'de yalnız arayüzler + home_domain; v1.1 implementasyon
    tests/{Unit,Integration,Contract}   tools/wsload
  api/
    proto/talky/v1/*.proto   WS olay sözleşmesi (tek kaynak)
    openapi/talky.v1.json    build'de sunucudan üretilir
    fixtures/*.json          altın olay akışları
  clients/
    packages/{protocol,core,hooks,tokens,i18n,config,test-utils}
    apps/{web,desktop,mobile}
  deploy/
    windows/  Talky.Server.msi (WiX), Install-Talky.ps1, WinSW/livekit.xml, firewall.ps1
    livekit/  livekit.yaml (node_ip, 7881/7882, gömülü TURN, webhook)
    hyperv/   (yedek) Ubuntu medya appliance cloud-init
  docs/       mimari, kurulum, runbook, admin kılavuzu
```

Kritik dosyalar (ilk yazılacaklar):
- `server/src/Talky.Realtime/Hub/ConnectionHub.cs` — oturum kayıt, yerel conv indeksi, fan-out, backpressure, resume
- `server/src/Talky.Realtime/Cluster/MeshBus.cs` — gRPC tam-mesh, node keşfi, resync sinyali
- `server/src/Talky.Core/Chat/SendMessageHandler.cs` — seq atama transaction'ı, idempotency, olay yayını
- `server/src/Talky.Identity/Ad/ForestSyncJob.cs` — çok-forest sync, GC, nested group
- `server/src/Talky.Data/Migrations/00001_Init.cs` — şema ve indeksler
- `api/proto/talky/v1/events.proto` + `api/fixtures/*.json` — istemci sözleşmesi
- `clients/packages/core/src/sync/engine.ts`, `ws/client.ts`, `outbox/outbox.ts`
- `clients/apps/web/src/features/timeline/MessageList.tsx`
- `deploy/windows/Install-Talky.ps1`, `deploy/livekit/livekit.yaml`

---

## 9. Kilometre Taşları (riskli olan önce)

Süreler göstergedir (2 backend, 2 frontend, 1 mobil, 1 DevOps/QA varsayımı). Her taş yalnızca çıkış kriteri karşılanınca kapanır.

| # | Kapsam | Çıkış kriteri |
|---|---|---|
| **M0 İskelet + spike'lar** (2–3 hf) | Repo, CI, MSI + Windows servisi, EF şema v0, WS echo, proto→C#/TS üretimi, `talky doctor`. **Spike A**: LiveKit Windows Server'da `lk load-test`. **Spike B**: 2-forest lab'de Negotiate + trust. **Spike C**: react-virtuoso 100k mesaj | `msiexec` ile kurulan servis `/api/v1/ping` döner; LiveKit Windows kararı (kal / Hyper-V appliance) verildi; çok-forest Kerberos girişi çalıştı; 100k listede 60 fps |
| **M1 Kimlik** (3 hf) | `identity_sources`, çok-forest sync, Negotiate + OIDC (ADFS) + LDAP bind, oturumlar, RBAC, admin: kaynak yönetimi | Trust'lı ve trust'sız forest kullanıcıları web + RN'de giriş yapar; admin oturum iptal eder, WS 1 sn'de düşer |
| **M2 Gerçek zamanlı çekirdek** (4 hf, en riskli) | WS hub, zarf, DM/grup/kanal gönderim, seq/ack/idempotency, resume, geçmiş, okundu, **gRPC mesh** 2+ node, presence/typing; istemci `core` (sync motoru, outbox) sözleşme testleriyle | 15k bağlantı yük testi ve node-kill kaosu §6/§7 hedeflerini karşılar; **500M satırlık sentetik `messages` ile geçmiş p99 <50 ms, `/sync` p99 <100 ms**; Vitest + xUnit sözleşme paketi yeşil |
| **M3 Mesajlaşma tamamlama + Web** (4 hf) | Takım/kanal yönetimi, thread, reaksiyon, düzenle/sil, mention, **Lucene.NET arama rolü** (indexer, Türkçe analizör, ACL filtresi, rebuild komutu), admin paneli v1 (metrikler, hata raporları, indeks durumu); web UI tam kapsam, TR/EN, tema | Sözleşme fixture'ları v1 dondurulur; 20 kişilik pilot 1 hafta 0 kayıp; **500M mesajda global arama p99 <100 ms, indeks gecikmesi ≤2 s, rebuild tatbikatı**; bundle/perf bütçeleri CI'da |
| **M4 Masaüstü + Dosyalar** (3 hf) | Tauri kabuk, MSI + imza, updater (`/updates`), GPO sessiz kurulum, WebView2 offline; chunk'lı upload, AMSI, önizleme, blurhash, sürükle-bırak | İmzalı güncelleme turu; 3 offline makinede proxy/CA ile kurulum; 100 MB yükleme ağ kesintisinden kurtulur |
| **M5 Aramalar + Toplantılar + Push** (5 hf) | Oda/token/webhook, arama durum makinesi, çalma, cihaz seçimi; grid/speaker, ekran paylaşımı (WebView2), host kontrolleri, webinar modu; push-relay, web push | 1:1 kurulum <2 s; 100 sıralı arama 0 hata; 25 katılımcılı toplantı bellek tavanında; UDP kapalı istemci TURN/TLS ile <3 s katılır |
| **M6 Mobil** (6 hf, M3 sonrası paralel) | RN uygulaması, SQLite önbellek, push, callkeep, biyometrik, MDM config, Maestro | Push gecikmesi <5 s; öldürülmüş iOS uygulaması VoIP push ile çalar (<4 s); arka plan resume 0 kayıp; pilotta crash-free ≥99.5 % |
| **M7 Sertleştirme + GA** (3 hf) | Ağ bozulma matrisi, AG failover tatbikatı, rolling upgrade tatbikatı, 24 s soak, yedek/geri yükleme, runbook'lar, çoklu sekme, 50k sentetik yük | Tüm SLO'lar staging'de; RC 1.0 imzalı MSI + istemci paketleri |
| **v1.1** | Federasyon (mTLS gRPC, allowlist, ayna), SAML, mobil ekran paylaşımı, Egress kayıt, VP9 SVC, E2EE opsiyonu, misafir/lobi | — |

**M0 durumu (2026-11-24).** M0 kapsamı teslim edildi, çıkış kriterleri henüz karşılanmadı; taş **açık**.
- Teslim edilenler: repo; `talky.exe` host'u ve CLI (`serve`, `migrate`, `doctor` — 7 kontrol, `config validate`);
  EF şema v0 (`docs/data-model.md`); `/ws` el sıkışması (Hello/Welcome, Ping/Pong, drain, bağlantı bütçesi;
  `docs/protocol.md`); proto→C#/TS üretimi ve drift kontrolü; MSI + Windows servis kaydı ve kurulum betikleri;
  `@talky/core` WS istemcisi ve fixture oynatıcısı; iki CI tanımı (GitHub Actions ve Azure DevOps).
- Açık — çıkış kriteri 1 (`msiexec` ile kurulan servis `/api/v1/ping` döner): `deploy/windows/Test-TalkyInstall.ps1`
  yükseltilmiş oturumda atılabilir bir VM'de çalıştırılana kadar açık; hiç çalıştırılmadı.
- Açık — Spike A: yalnızca geliştirici makinesinde ön kontrol yapıldı (`docs/livekit-windows-spike.md`); gerçek
  Windows Server donanımında ölçüm bekliyor, kal / Hyper-V kararı M1 başında.
- Açık — Spike B: çalıştırılmadı; iki forest'lı lab'de M1'in ilk işi.
- Açık — Spike C: çalıştırılmadı; M3 açılışına alınması önerildi, sahibinin kararı bekleniyor.
- Açık — CI hiç çalışmadı: repo'nun uzak deposu yok. Barındırma (GitHub mı Azure DevOps Server mı) sahibin kararı;
  karar sonrası tek tanım kalır.
- Bilerek sonraya bırakılanlar: servis çıkış kodu / SCM kurtarma davranışı (gerçek servis kurulumu ister), rol
  kapılama (M2; roller bugün yalnızca raporlanır), encode-once fan-out (M2), SQL'e göre saat kayması `doctor`
  kontrolü (M2), OpenAPI üretimi (M1, ilk gerçek REST uç noktalarıyla). §5'teki AD/GC, pay ve LiveKit `doctor`
  kontrolleri ilgili modüllerle gelir.
- Plan metnine notlar: §3.3'teki `?v=1` geçersizdir — uç nokta `/ws`'dir, sürüm `Hello.protoVersion` ile taşınır.
  §3.5: UUIDv7 yalnızca `messages.id` içindir, diğer PK'ler EF sıralı GUID'dir; `users.sid`/`upn` şema v0'da yoktur,
  M1'de ek göçle gelir (`docs/data-model.md`).

---

## 10. Riskler ve Karşı Önlemler

| Risk | Önlem |
|---|---|
| LiveKit'in Windows'ta üretim ölçeğinde performansı kanıtlanmamış | M0 Spike A; yedek: Hyper-V üzerinde Ubuntu appliance (IT açısından yine Windows Server altyapısı) |
| Trust'sız forestlarda Kerberos yok | LDAP bind / forest'ın ADFS'i ile OIDC; tasarım her kaynağa ayrı yol izin verir |
| iOS gelen arama push'u internet çıkışı ister | DMZ push-relay, allowlist; çıkış yoksa iOS yalnız ön planda çalar — yazılı kabul |
| Özel küme bus'ı hata kaynağı olabilir | Doğruluk DB seq + istemci resync'e dayanır, bus yalnız hızlandırıcı; kaos testleri; NATS arayüz arkasında alternatif |
| Federasyon kapsamı büyük | v1.1'e; v1'de `home_domain`, `IEventSink`, imzalanabilir olaylar hazır |
| SQL Server lisansı | Standard yeterli (Basic AG, compression). Lisans engelse PostgreSQL 17 Windows'ta çalışır; M0'da tek DB seçilir, ikisi birden desteklenmez |
| Lucene.NET 4.8 "beta" etiketi | Orchard Core / Umbraco (Examine) üretimde kullanır; indeks yalnız cache, SQL'den yeniden inşa edilir; sürüm sabitlenir |
| Geçmiş tablosu milyar satıra ulaşır | Clustered `(conversation_id, seq)` ile maliyet tablo boyutundan bağımsız; M2'de 500M satırla kanıt; retention anahtar boyunca partili silme |
| .NET ekibinde Rust (Tauri) bilgisi | Rust kısmı küçük ve çoğu plugin yapılandırması; C2'de Electron yedek kararı |

---

## 11. Doğrulama (uçtan uca nasıl test edilir)
1. **Geliştirici makinesi (Windows 11)**: SQL Server Developer + `talky.exe --role all` + `livekit-server.exe --dev` + `pnpm dev`; `talky doctor` yeşil.
2. **Sözleşme**: `dotnet test` (Contract) ve `pnpm test` aynı `api/fixtures` ile geçer; `buf generate` + OpenAPI üretimi sonrası `git diff` boş.
3. **Fonksiyonel E2E**: Playwright ile iki tarayıcı bağlamı: giriş (Negotiate mock / LDAP), DM gönder-al, ağ kes-bağlan (0 kayıp, dup yok), 1:1 arama `framesDecoded>0`, ekran paylaşımı, dosya yükle-önizle.
4. **Yük/Kaos**: `tools/wsload` 15k bağlantı + node kill; `lk load-test`; `tools/seed` ile 500M sentetik mesaj → geçmiş/sync/arama p99 ölçümü; search node kill → diğer node cevaplar; sonuçlar admin panelinde ve CI artefaktında.
5. **AD lab**: 2 forest + trust + trust'sız forest; sync raporu, Kerberos girişi (Edge ve Tauri), LDAP formu, ADFS OIDC.
6. **Kurulum tatbikatı**: temiz Windows Server 2025'e `Install-Talky.ps1` + MSI; ikinci node ekle → mesh'e katıldığı admin panelinde görünür; rolling upgrade sırasında istemcide kesinti ≤5 sn.
7. **Mobil**: Maestro akışları; killed-app VoIP push; arka plandan dönüşte resume.
