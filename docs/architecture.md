# Talky mimarisi

Kurumsal, tamamen on-prem, Teams/Skype for Business tarzı iletişim platformu: mesajlaşma,
1:1 arama, grup toplantısı + ekran paylaşımı, dosya paylaşımı. Hedef ölçek **10.000–50.000
kullanıcı**. Üretim yalnızca **Windows Server 2022/2025** üzerinde çalışır.

Bu belge onaylanmış kararları ve bunların sonuçlarını özetler. Protokol davranış kuralları
için `docs/protocol.md`, kurulum ve işletim için `docs/operations.md`.

## 0. Tasarımı yöneten üç kural

1. **Çok hızlı.** Sıcak yollar (mesaj gönderimi, geçmiş, sync, fan-out) ölçülür; her
   değişiklik ölçümle gelir.
2. **Hatasız.** Doğruluk hiçbir zaman küme bus'ına dayanmaz. Tek gerçek kaynak SQL'deki
   konuşma başına `seq` sayacıdır; istemci resync'i her boşluğu kapatır.
3. **Mümkün olduğunca az ayrı ürün.** Her yeni ürün, müşterinin yedeklemesi, yamalaması,
   izlemesi ve lisanslaması gereken bir şeydir. Bir özellik ayrı bir ürün gerektiriyorsa,
   önce onsuz yapılıp yapılamayacağı sorulur.

## 1. Üretim ayak izi

Üretimde tam olarak üç ürün vardır. Docker yoktur.

```
                          ┌───────────────────────────────┐
   İstemciler             │  Yük dengeleyici (müşterinin)  │
   web / masaüstü / mobil │  TCP passthrough veya TLS      │
        │                 │  sonlandırma, /readyz probu    │
        ▼                 └───────────────┬───────────────┘
   talky.corp:443                         │
                                          ▼
   ┌──────────────────────────────────────────────────────────────────────┐
   │  Talky düğümleri  —  talky.exe (.NET 10, tek binary, Windows servisi) │
   │                                                                      │
   │   node-01 --role api      node-02 --role api      node-03 --role api │
   │   node-04 --role search   node-05 --role search                      │
   │   :8443 TLS (Kestrel)  ·  :8080 opsiyonel HTTP  ·  :9443 gRPC mesh   │
   │                                                                      │
   │   ╰──────── gRPC full-mesh küme bus'ı (her düğüm her düğüme) ───────╯ │
   └───────┬──────────────────────────┬─────────────────────┬─────────────┘
           │                          │                     │
           ▼                          ▼                     ▼
   ┌───────────────┐        ┌──────────────────┐   ┌──────────────────┐
   │ SQL Server    │        │ SMB / DFS payı   │   │ LiveKit düğümleri│
   │ 2025 (AG)     │        │ dosyalar         │   │ medya SFU + TURN │
   │ tek gerçek    │        │ IBlobStore       │   │ Windows servisi  │
   │ kaynak        │        │                  │   │ 7880/7881/7882   │
   └───────────────┘        └──────────────────┘   └──────────────────┘

   DMZ:  talky.exe --role push-relay  →  FCM / APNs
```

| Ürün | Ne | Neden ayrı |
|---|---|---|
| `talky.exe` | .NET 10, self-contained tek dosya, Windows servisi, gMSA altında | Uygulamanın tamamı |
| SQL Server 2025 | Tek gerçek kaynak; AG ile yüksek erişilebilirlik | Müşteride zaten var, DBA ekibi zaten var |
| LiveKit | Medya SFU + gömülü TURN | WebRTC SFU yazmak ayrı bir ürün geliştirmektir |

**Kasten dışarıda bırakılanlar** ve yerine ne konduğu:

| Bırakılan | Yerine | Gerekçe |
|---|---|---|
| NATS / RabbitMQ | gRPC full-mesh küme bus'ı | Bus yalnızca fan-out hızlandırıcısıdır; doğruluk SQL'dedir, bu yüzden bir mesaj ürününün dayanıklılık garantilerine ihtiyaç yoktur |
| Kuyruk ürünü | SQL'de `jobs` tablosu | İş hacmi (bildirim, indeksleme, temizlik) SQL için önemsiz; ayrı bir ürün yedekleme ve izleme yükü demek |
| Elasticsearch | Gömülü Lucene.NET, `--role search` | Aynı motorun aynı indeksi; ayrı bir JVM kümesi olmadan |
| MinIO / S3 | SMB/DFS payı, `IBlobStore` arkasında | Müşterinin dosya altyapısı zaten SMB/DFS; soyutlama ileride S3'ü mümkün kılar |
| Redis | Yok | LiveKit düğümleri bağımsızdır; odaları backend yerleştirir |
| coturn | LiveKit gömülü TURN | Aynı işi yapan ikinci bir ürün |
| Prometheus + Grafana | Yerleşik yönetim paneli | Metrikler zaten üründe; müşteri ayrıca iki ürün işletmek zorunda kalmaz |
| Docker | Yok | Üretim Windows Server; MSI + PowerShell yeterli ve müşteriye tanıdık |
| ClamAV | Windows Defender AMSI | Sunucuda zaten çalışıyor |

## 2. `talky.exe` — modüler monolit ve roller

Tek assembly ailesi, tek binary, tek yapılandırma dosyası. Modüller derleme zamanında
ayrıdır (`Talky.Realtime`, `Talky.Identity`, `Talky.Media`, `Talky.Files`, `Talky.Search`,
`Talky.Push`, `Talky.Federation`, `Talky.Data`, `Talky.Protocol`, `Talky.Core`), çalışma
zamanında aynı süreçtedir. Mikroservis sınırı yerine **rol** sınırı vardır:

| Rol | Ne yapar | Nerede çalışır |
|---|---|---|
| `api` | HTTP/REST, WebSocket oturumları, gRPC mesh, LiveKit sinyal proxy'si | Yük dengeleyicinin arkasında, 3–6 düğüm |
| `worker` | `jobs` tablosunu tüketir: bildirim, indeksleme kuyruğu, saklama/temizlik | `api` ile aynı düğümde veya ayrı |
| `search` | Gömülü Lucene.NET indeksini tutar ve sorgular | 2 düğüm, NVMe, çok RAM |
| `push-relay` | DMZ'den FCM/APNs'e giden tek yön | DMZ, 1–2 düğüm |
| `all` | Hepsi birden | Tek sunucu kurulumları ve laboratuvar |

Rol yalnızca hangi hosted service'lerin başlatıldığını belirler; kod tabanı aynıdır. Bir
müşteri tek sunucuda `--role all` ile başlar, büyüdüğünde aynı binary'yi farklı rollerle
dağıtır. Yeni ürün yok, yeni yapılandırma biçimi yok.

`talky doctor` her düğümde çalışır: yapılandırma yer tutucuları, SQL erişimi ve şema
sürümü, sertifika, dosya payı, LiveKit erişilebilirliği, mesh eşleri, disk ve saat.

## 3. Küme: gRPC full-mesh bus

Her `api` düğümü diğer her `api` düğümüne 9443 üzerinden kalıcı, çift yönlü bir gRPC akışı
açar (`api/proto/talky/cluster/v1/mesh.proto`). N düğüm için N·(N−1)/2 bağlantı; 6 düğümde
15 bağlantı. Bu ölçekte broker'a gerek yoktur.

Bus'ın taşıdığı tek şey **ipuçlarıdır**: "şu konuşmada `seq` şuraya ilerledi", "şu kullanıcı
şu düğümde çevrimiçi", "şu oturumu boşalt". Bus'ın kaybettiği bir çerçeve veri kaybına yol
açmaz:

1. Yazan düğüm mesajı SQL'e yazar ve `(conversation_id, seq)` alır — **burada işlem biter**.
2. Bus, mesajı diğer düğümlerdeki açık WebSocket'lere iter (hızlı yol).
3. Bir düğüm ipucunu kaçırırsa, istemci `Ping`/yeniden bağlanma sırasında `since`
   haritasıyla boşluğu fark eder ve sunucu eksik `seq` aralığını SQL'den doldurur;
   dolduramazsa `ResyncRequired` gönderir ve istemci `GET /api/v1/sync` çağırır.

Yani bus **en iyi çaba**dır ve bilinçli olarak öyle tasarlanmıştır. Bir düğüm düşerse
diğerleri bağlantıyı yeniden kurar; mesh üyeliği yapılandırmadaki `Talky:Cluster:Seeds`
listesinden gelir.

## 4. Gerçek zamanlı protokol (özet)

Ayrıntı ve altın fixture'lar: `docs/protocol.md`, `api/proto/talky/v1/frames.proto`.

- Bir WebSocket **text** çerçevesi = bir proto3-JSON nesnesi (`ClientFrame`/`ServerFrame`);
  `oneof body` alan adı çerçeve tipidir. Azami çerçeve 64 KiB.
- El sıkışma: istemci 5 sn içinde `Hello` gönderir, sunucu `Welcome` ile yanıtlar.
- İki ayrı canlılık mekanizması: WebSocket protokol ping/pong (sunucu → istemci ölü mü) ve
  uygulama seviyesi `Ping`/`Pong` (istemci → sunucu ölü mü, varsayılan 25 sn).
- Yeniden bağlanma: üstel geri çekilme 0,5 sn → 30 sn, ±%20 jitter. `Reconnect{delayMs}`
  alındıysa ilk deneme o pencerede rastgelelenir — sürüm yükseltmede sürü etkisini bu önler.
- İstek/yanıt `rid` (UUIDv7) ile eşleşir; sunucu **tam olarak bir** `Ack` veya `Error`
  döndürür. Gönderimler `clientMsgId` ile idempotenttir.
- İstemci aynı `id` veya aynı `(conversationId, seq)` ikinci kez gelirse atar.

## 5. Veri modeli (özet)

Ayrıntı ve şema göçleri `Talky.Data` paketinin sorumluluğundadır; burada mimariyi
belirleyen kararlar özetlenir.

- **Sıralamanın tek kaynağı**: her konuşmanın kendi `seq` sayacı vardır ve mesajlar
  `(conversation_id, seq)` ile benzersizdir. Global bir sayaç yoktur — konuşma başına sayaç,
  fan-out ve resync'i düğümden bağımsız kılar.
- Ana tablolar: `users`, `devices`, `conversations`, `conversation_members`, `messages`,
  `message_attachments`, `receipts` (teslim/okundu), `presence`, `jobs`, `nodes`.
- **`jobs`**: kuyruk ürünü yerine SQL tablosu. `worker` rolü satırları `UPDATE … OUTPUT` ile
  kilitleyip işler; yeniden deneme sayacı ve görünürlük zaman damgası satırdadır. İş türleri:
  push bildirimi, arama indeksleme, saklama süresi dolmuş içeriğin temizliği, virüs tarama
  sonucu işleme.
- Geçmiş sorguları `(conversation_id, seq DESC)` üzerinde keyset sayfalamayla çalışır;
  OFFSET kullanılmaz.
- Yüksek erişilebilirlik SQL Server Always On Availability Group ile sağlanır; uygulama
  yalnızca dinleyici adını bilir.

## 6. Kimlik: çok ormanlı Active Directory

Müşteri tek bir ormanda değildir; birleşmeler ve satın almalar nedeniyle birden çok orman
ve güven ilişkisi vardır. v1'de üç yol desteklenir:

| Yol | Kullanım | Not |
|---|---|---|
| Negotiate / Kerberos | Domain'e katılmış masaüstü istemcileri | SPN `HTTP/talky.corp`; kullanıcı hiçbir şey yazmaz |
| ADFS / OIDC | Web ve mobil, ormanlar arası | Yetkilendirme kodu + PKCE |
| LDAP bind | Kerberos'un ulaşamadığı orman veya hizmet hesapları | Yalnızca gerektiğinde |

Kullanıcı dizini periyodik olarak senkronlanır (orman başına bir bağlayıcı); `objectSid` +
orman kimliği birincil eşleştirme anahtarıdır, UPN değil — UPN değişebilir.

SAML ve **kuruluşlar arası federasyon** (iki ayrı Talky kurulumunun birbiriyle mesajlaşması)
v1.1'e bırakılmıştır; `Talky.Federation` bugün yalnızca sınır arayüzlerini tutar.

## 7. Medya: Windows üzerinde LiveKit

- **Sinyalleşme istemciye doğrudan açılmaz.** Kestrel TLS'i sonlandırır ve `talky.exe`
  içindeki YARP (kütüphane olarak, ayrı bir ürün olarak değil) `/media/{node}/rtc` yolunu
  ilgili LiveKit düğümünün 7880 portuna proxy'ler. Böylece istemci tek bir isim
  (`talky.corp`) ve tek bir sertifika görür; LiveKit düğümleri iç ağda kalır.
- **Oda yerleşimini backend yapar.** Redis yoktur. Bir toplantı başladığında `api` rolü,
  yükü ve yakınlığı gözeterek bir LiveKit düğümü seçer, o düğüm için bir token üretir ve
  istemciye verir. Bir oda tek bir düğümde yaşar.
- **Gömülü TURN.** coturn yoktur. UDP 3478 ve TLS 5349; aktarım tahsisleri
  30000–40000/UDP aralığından port alır (bu aralık güvenlik duvarında açık olmalıdır).
- **Yaşam döngüsü webhook'ları** `https://talky.corp/internal/livekit/webhook` adresine
  gelir; backend odanın boşaldığını böyle öğrenir (`empty_timeout` 60 sn,
  `departure_timeout` 15 sn).
- **Windows sınırlaması:** LiveKit'in kendi kapasite yönetimi Windows'ta devre dışıdır
  (`hwstats: CPU monitoring unsupported`). Yerleşim kararı zaten backend'de olduğu için
  tasarım bundan etkilenmez, ama düğüm yükü LiveKit'ten değil, düğümün performans
  sayaçlarından okunur. Ayrıntı ve Spike A ölçütleri: `docs/livekit-windows-spike.md`.

## 8. Dosyalar: SMB / DFS

- `IBlobStore` arkasında tek uygulama: SMB/DFS payı. Yol şeması içerik adresli
  (`\\pay\talky\files\<yıl>\<ay>\<hash-önek>\<blobId>`), böylece dizin başına dosya sayısı
  kontrollüdür ve aynı dosyanın iki kopyası tek blob olur.
- Yükleme parçalıdır ve doğrudan `api` düğümünden paya yazılır; SQL yalnızca meta veriyi
  tutar. Büyük dosya veritabanına hiç girmez.
- **Virüs taraması Windows Defender AMSI ile** yapılır: `worker` rolü yüklenen bloğu AMSI'ye
  verir, temiz değilse blob karantinaya alınır ve gönderen bilgilendirilir. Ayrı bir
  antivirüs ürünü yoktur.
- Erişim denetimi paya değil uygulamaya aittir: pay yalnızca gMSA'ya açıktır, indirme
  bağlantıları kısa ömürlü ve konuşma üyeliğine bağlıdır.

## 9. Arama: gömülü Lucene.NET

- `--role search` düğümleri Lucene.NET indeksini yerel NVMe üzerinde tutar
  (`<DataDir>\index`). Ayrı bir arama kümesi, JVM, ayrı yedekleme yoktur.
- İndeksleme `jobs` üzerinden asenkrondur: mesaj SQL'e yazıldıktan sonra bir iş satırı
  düşer, `search` düğümü onu tüketir. Arama bu yüzden **nihai tutarlıdır**; mesajın
  görünürlüğü ise SQL'e yazıldığı anda kesindir.
- İndeks **türetilmiş veridir**. Yedeklenmesi gerekmez; kaybolursa SQL'den yeniden
  kurulur. Bu, yedekleme planını tek bir ürüne (SQL) ve bir paya indirger.
- Türkçe için ayrıca analiz zinciri gerekir (küçük harfe indirme, ASCII katlama, Türkçe
  gövdeleme); İngilizce ve Türkçe alanlar ayrı analiz edilir.

## 10. Push relay

Mobil bildirim FCM ve APNs'e gitmek zorundadır, yani internete çıkış gerekir. İç ağdaki
`api` düğümlerine internet erişimi vermemek için:

- DMZ'de `talky.exe --role push-relay` çalışır. Aynı binary, aynı yapılandırma biçimi.
- İç düğümler relay'e yalnızca **tek yönlü** ve içerik olarak minimum bilgi gönderir:
  hedef cihaz jetonu, sessiz uyandırma yükü ve rozet sayısı. Mesaj metni relay'den geçmez.
- Relay'in SQL'e erişimi yoktur.

## 11. Yönetim paneli ve gözlemlenebilirlik

- Metrikler ve sağlık, `talky.exe` içindeki yerleşik yönetim panelindedir; Prometheus ve
  Grafana kurulmaz. Panel düğüm başına canlı sayaçları (açık WebSocket, saniyedeki mesaj,
  `jobs` gecikmesi, mesh eş durumu, SQL gecikmesi, LiveKit düğüm durumu) ve son 24 saatin
  özetini gösterir.
- Yapılandırılmış loglar `%ProgramData%\Talky\logs` altına Serilog ile JSON olarak yazılır;
  uyarı ve üzeri ayrıca Windows Olay Günlüğü'ne gider. Müşterinin bir log toplayıcısı varsa
  bu dosyaları okur — zorunlu değildir.
- `/readyz` yük dengeleyici probudur ve boşaltma sırasında bilinçli olarak 503 döner
  (bkz. `docs/operations.md`, yuvarlanan yükseltme).

## 12. Ölçekleme ve performans bütçesi

10.000–50.000 kullanıcı için hedef yerleşim:

| Bileşen | Adet | Boyut |
|---|---|---|
| `api` düğümü | 3–6 | 8 vCPU / 16 GB |
| `search` düğümü | 2 | 64 GB RAM, NVMe |
| SQL Server 2025 | AG (2 replika + tanık) | Müşterinin standart DB donanımı |
| LiveKit düğümü | 2–6 | 16 vCPU / 32 GB / 10 GbE |
| `push-relay` | 1–2 | DMZ, küçük |

Performans bütçesi. Bunlar **hedeftir** ve M1'den itibaren `Talky.WsLoad` ile sürekli
ölçülür; aşan bir değişiklik birleştirilmez.

| Yol | Bütçe (p95) | Not |
|---|---|---|
| Mesaj gönderimi (`Ack` dönene kadar, sunucu tarafı) | < 150 ms | SQL yazma + `seq` tahsisi dahil |
| Aynı düğümdeki alıcıya fan-out | < 50 ms | |
| Başka düğümdeki alıcıya fan-out (mesh) | < 200 ms | Bus en iyi çaba; bütçe aşılırsa mesaj yine de kaybolmaz |
| Geçmiş sayfası (50 mesaj) | < 100 ms | Keyset sayfalama |
| `GET /api/v1/sync` | < 300 ms | 50 konuşmaya kadar |
| Arama sorgusu | < 500 ms | Nihai tutarlı indeks |
| Toplantıya katılma (token → ilk medya) | < 2 sn | TURN'e düşen istemcide < 3 sn |
| Düğüm başına eşzamanlı WebSocket | 3.000–5.000 | 8 vCPU / 16 GB düğümde |
| Yeniden başlatmada yeniden bağlanma dalgası | 30 sn'ye yayılır | `Reconnect{delayMs}` jitter'ı |

## 13. Riskler

| Risk | Etki | Karşılık |
|---|---|---|
| LiveKit Windows'ta hedef yükü kaldırmaz | Ayak izi dört ürüne çıkar | Spike A ölçütleri ve Hyper-V Linux cihaz yedeği hazır (`docs/livekit-windows-spike.md`) |
| gRPC mesh N² bağlantı, düğüm sayısı büyürse | Bağlantı ve CPU maliyeti | 6 düğümde 15 bağlantı; sınır aşılırsa bus'ı hub düğümlere indirgemek mümkün — doğruluk SQL'de olduğu için bu bir veri riski değil |
| Çok ormanlı AD'de senkronizasyon karmaşıklığı | Yanlış kullanıcı eşleşmesi | `objectSid` + orman kimliği anahtar; UPN asla anahtar değil |
| Lucene.NET indeksinin Türkçe kalitesi | Kötü arama sonuçları | Ayrı Türkçe analiz zinciri; altın sorgu kümesiyle regresyon testi |
| SMB payı gecikmesi veya DFS yönlendirmesi | Yavaş yükleme/indirme | `IBlobStore` soyutlaması; pay gecikmesi `talky doctor` ve panelde ölçülür |
| Tek binary'nin dağıtım boyutu (~120 MB) | Yükseltme süresi | MSI ~49 MB; yuvarlanan yükseltmede düğüm başına birkaç dakika |
| AMSI taramasının büyük dosyalarda maliyeti | Yükleme gecikmesi | Tarama asenkron, `jobs` üzerinden; blob temiz sonuç gelene kadar karantinada |
| Yönetim paneli, olgun bir izleme yığınının yerini tutmaz | Operasyon körlüğü | Loglar dosya olarak dışa açık; müşteri isterse kendi toplayıcısını bağlar |
