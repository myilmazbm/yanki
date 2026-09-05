# Talky veri modeli (v0)

Tek gerçek kaynak: `server/src/Talky.Data`. Varlıklar (POCO) `Talky.Core/Domain` altında, EF Core
yapılandırmaları `Talky.Data/Configurations` altındadır. Şema `Migrations/20260905131614_Init` ile
oluşturulur.

Hedef: **SQL Server 2025** (üretimde Always On AG, geliştirmede `(localdb)\MSSQLLocalDB` 17.0).
10.000–50.000 kullanıcı, tek `talky.exe`, ayrı bir kuyruk/önbellek ürünü yok: iş kuyruğu da küme
üyeliği de bu şemadadır.

## Genel kurallar

| Kural | Değer | Gerekçe |
|---|---|---|
| Ad biçimi | `snake_case` tablo ve kolon | `SnakeCaseNaming.UseSnakeCaseNames()` (aşağıda) |
| Zaman | `datetime2(3)`, daima UTC | 8 yerine 6 bayt; milisaniye zaten saatlerimizin ve tel protokolünün çözünürlüğü |
| Metin | `nvarchar(n)`, gövdeler `nvarchar(max)` | Kurumsal ortamda Türkçe/Unicode zorunlu |
| Enum | `tinyint`, açık sayısal değerlerle | Sıcak satırlarda alan başına 3 bayt tasarruf; değer haritaları aşağıda |
| İzolasyon | `READ_COMMITTED_SNAPSHOT ON` | Okuyucu asla yazıcının arkasında beklemez; hiçbir okuma yolunda `NOLOCK` yok |
| Sayfalama | Yalnız keyset (`seq < @before`) | `OFFSET` ve `COUNT(*)` yasak |
| Okuma izleme | `NoTracking` (varsayılan) | Değişiklik izleyicisi üzerinden yazan yol yok |
| Guid PK | Kümelenmiş + `SequentialGuidValueGenerator` | SQL Server sıralaması için üretilir (aşağıda) |
| `home_domain` | `users`, `teams`, `conversations` | Federasyona (v1.1) hazır; v1'de tek yerel alan adı |

## ER genel görünümü

```
identity_sources 1─────* users ─┬──* devices ──* sessions
                                │        ^________|  (session.device_id)
                                └──* sessions

teams 1──* team_members *──1 users
teams 1──* conversations            (yalnız type = channel)

conversations 1──1 dm_pairs         (yalnız type = dm, user_a < user_b)
conversations 1──* conversation_members *──1 users
conversations 1──* messages         (FK yok; bkz. "messages ve yabancı anahtarlar")
                       │
                       ├──* reactions          (message_id = messages.id)
                       ├──* attachments        (message_id = messages.id, yüklerken NULL)
                       └──1 thread_meta        (root_message_id = messages.id)

conversations 1──* calls 1──* call_participants

İşletim (uygulama grafiğinden bağımsız):
jobs · cluster_nodes · metrics_1m · client_errors · audit_logs · federation_peers
```

Yabancı anahtarlar yalnızca kimlik ve üyelik tablolarında vardır: `users → identity_sources`,
`team_members → teams|users`, `conversations → teams`, `dm_pairs → conversations`,
`conversation_members → conversations|users`, `devices → users`, `sessions → users|devices`.
Mesaj kapsamındaki tablolar (`messages`, `reactions`, `attachments`, `thread_meta`) ve arama/çağrı
geçmişi (`calls`, `call_participants`) bilinçli olarak FK taşımaz — gerekçe aşağıda.

## Tablolar

### Kimlik

**`identity_sources`** — Kullanıcıların geldiği dizin (yerel, AD, LDAP, OIDC, SAML). `name` benzersiz.
`config` JSON'dur (bind DN, filtre, uç noktalar); sırlar burada değil DPAPI'dedir.
Kısıt: `ck_identity_sources_config_json`.

**`users`** — Kişi veya bot. PK kümelenmiş sıralı GUID.
- `UQ (home_domain, user_name)`: giriş adı alan adı içinde benzersizdir, küresel değil (federasyon).
- `UQ (identity_source_id, external_id) WHERE external_id IS NOT NULL`: dizin nesnesi başına bir satır;
  yerel hesaplarda `external_id` NULL olduğu için filtre şart.
- `IX (email) WHERE email IS NOT NULL`: davet ve arama.
- Silme **yumuşaktır** (`state = Deleted`): eski mesajların göndereni her zaman çözülebilsin diye
  mezar taşı kalır.

**`devices`** — Kullanıcının bir istemci kurulumu; push jetonu burada durur.
- `UQ (push_token) WHERE push_token IS NOT NULL`: aynı jeton iki cihazda kayıtlıysa fan-out çift teslim eder.
- `IX (user_id)`: "cihazlarım" ve push fan-out.

**`sessions`** — Kimliği doğrulanmış oturum. Jetonun kendisi saklanmaz, yalnız SHA-256 özeti
(`token_hash`, `UQ`). Kimlik doğrulama tek bir `token_hash` seek'idir.
- `IX (user_id)`: "oturumlarım", toplu iptal.
- `IX (expires_at)`: süresi dolanların süpürülmesi.
- `user_id` FK'si `NO ACTION`'dır (`CASCADE` değil): kullanıcı normalde yumuşak silinir ve
  `users → devices → sessions` zinciri zaten kaskad olduğu için ikinci bir kaskad yolu
  SQL Server tarafından reddedilirdi.

### Takımlar

**`teams`** — Kanal ve üyelik sahibi. `UQ (home_domain, slug)`.

**`team_members`** — PK `(team_id, user_id)` kümelenmiş; `IX (user_id)` "hangi takımlardayım".

### Konuşmalar

**`conversations`** — DM, grup veya kanal. Şemanın kalbi: `last_seq`.
- `last_seq bigint`: bu konuşmada dağıtılmış en yüksek sıra numarası. Ekleme işlemi bunu kendi
  işlemi içinde artırır; satır kilidi toplam sırayı üretir (aşağıya bakın).
- `last_message_at`: konuşma listesinin sıralaması; hiçbir zaman `MAX(seq)` sorgulanmaz.
- `UQ (team_id, slug) WHERE team_id IS NOT NULL AND slug IS NOT NULL`: kanal kısa adı takım içinde benzersiz.
- `CHECK (last_seq >= 0)`.

**`dm_pairs`** — Bire bir konuşmanın benzersizlik indeksi: PK `conversation_id`,
`UQ (user_a, user_b)`, `CHECK (user_a < user_b)`.
Karşılaştırma SQL Server'ın `uniqueidentifier` sıralamasına göredir (son bayt grubundan başlar),
.NET'in `Guid.CompareTo` sıralamasına göre **değil**. C# tarafında sırayı `DmPair.Order(a, b)` kurar;
`Guid.CompareTo` kullanmak kısıt ihlaline yol açar.

**`conversation_members`** — PK `(conversation_id, user_id)` kümelenmiş.
- `last_read_seq` + `mention_count`: **okunmamış sayısı hiçbir zaman hesaplanmaz**, çıkarmadır:
  `conversations.last_seq - conversation_members.last_read_seq`. `messages` üzerinde `COUNT(*)` yapan
  hiçbir yol yoktur.
- `IX (user_id) INCLUDE (last_read_seq, mention_count, muted, notify_level)`: "konuşma listem"
  sorgusu kullanıcıya seek eder ve imleç kolonlarını doğrudan indeksten okur.
- `CHECK (last_read_seq >= 0 AND mention_count >= 0)`.

### Mesajlar

**`messages`** — Kümelenmiş PK `(conversation_id, seq)`. Ayrıntılı gerekçe aşağıda.
- `id uniqueidentifier`: **UUIDv7** (`Guid.CreateVersion7()`), `UQ NONCLUSTERED`. Dışarıya açık
  kimlik; tepkiler, konu başlıkları ve federasyon bunu kullanır, sıralama için asla kullanılmaz.
- `UQ (sender_id, client_msg_id)`: gönderimi **idempotent** yapar.
- `IX (parent_id, seq) INCLUDE (conversation_id) WHERE parent_id IS NOT NULL`: konu görünümü.
- `body nvarchar(max)`, `metadata nvarchar(max)` + `CHECK (metadata IS NULL OR ISJSON(metadata) = 1)`.
- `deleted_at`: silme yumuşaktır, satır kalır — `seq` boşluksuz kalsın diye.
- `DATA_COMPRESSION = PAGE` (tüm indeksler).

**`thread_meta`** — PK `root_message_id` (= `messages.id`). `reply_count`, `last_reply_seq`,
`last_reply_at`, `participant_count` denormalize sayaçlardır; konu listesi için sayım yapılmaz.
`IX (conversation_id, last_reply_at)`.

**`reactions`** — PK `(message_id, user_id, emoji)` kümelenmiş. Bir mesajın tepkileri fiziksel olarak
bitişiktir; sayfa yükleme `message_id IN (...)` ile tek bir aralık taramasıdır.

**`attachments`** — Blob dosya deposunda, satır burada. `UQ (object_key)`, `IX (sha256)` (içerik
adresli tekilleştirme), `IX (message_id) WHERE message_id IS NOT NULL` (mesaj oluşmadan önce yükleme
başladığı için NULL olabilir), `IX (scan_status, created_at)` (virüs tarayıcı kuyruğu).

### Aramalar

**`calls`** — LiveKit odasının kalıcı kaydı. `UQ (room_name)`,
`IX (conversation_id, started_at) WHERE conversation_id IS NOT NULL`,
`IX (state) WHERE state < 2` (düğüm yeniden başlarken çalan/aktif çağrıları kurtarmak için).

**`call_participants`** — PK `(call_id, user_id)`; `IX (user_id, invited_at)` "arama geçmişim".

### İşletim

**`jobs`** — Arka plan işleri. Ayrı bir broker ürünü yoktur.
- `IX (state, run_at) INCLUDE (type) WHERE state = 0`: talep (claim) sorgusunun tek indeksi.
- `IX (locked_at) WHERE state = 1`: ölü işçinin bıraktığı talebin süpürülmesi.
- `UQ (dedupe_key) WHERE dedupe_key IS NOT NULL AND state < 2`: anahtar başına en fazla bir canlı iş.
- `IX (completed_at) WHERE completed_at IS NOT NULL`: bitmiş işlerin saklama süpürmesi.
- `CHECK (attempts >= 0 AND max_attempts >= 1)`.

**`cluster_nodes`** — PK `node_id` (nvarchar 128) kümelenmiş, `IX (last_seen_at)`. Yalnızca keşif
amaçlıdır: doğruluk hiçbir zaman bu tabloya dayanmaz. `roles` `NodeRole` bit maskesidir (`int`).

**`metrics_1m`** — PK `(bucket, node_id, name)` kümelenmiş. Zaman ilk kolondur, böylece hem panonun
aralık sorgusu hem saklama silmesi kümelenmiş aralık taramasıdır. `IX (name, bucket)`.

**`client_errors`** — İstemcilerden gelen hata raporları. `occurred_at` istemci saatidir ve sıralama
için asla güvenilmez; `received_at` sunucu saatidir ve indekslenir. `context` JSON + `ISJSON` kısıtı.

**`audit_logs`** — **Yalnızca ekleme.** Uygulama kodu bu tabloda `UPDATE`/`DELETE` yapmaz; saklama
süpürmesi dışında satır silinmez. `actor_name` yazma anında kopyalanır ki kullanıcı silinse bile
kayıt anlamlı kalsın. Sıralı GUID PK sayesinde ekleme kümelenmiş indeksin sonuna düşer.
`IX (at)`, `IX (actor_id, at) WHERE actor_id IS NOT NULL`,
`IX (target_type, target_id, at) WHERE target_id IS NOT NULL`.

**`federation_peers`** — v1.1 için şema yer tutucusu. v1'de hiçbir kod okumaz; federasyonu açan
göçün tamamen ekleme (additive) olması için şimdiden vardır. `UQ (domain)`.

## Neden `messages` kümelenmiş PK'si `(conversation_id, seq)`

1. **Fiziksel yerellik.** Bir konuşmanın tüm geçmişi kümelenmiş indekste tek bir bitişik aralıktır.
   "N'inci sıradan önceki 50 mesaj" sorgusu geriye doğru tek bir aralık taramasıdır: sıralama yok,
   anahtar arama (key lookup) yok, `OFFSET` yok.
2. **Doğal sayfalama anahtarı.** İstemciler zaten `seq` ile senkronize olur (bkz. `docs/protocol.md`),
   dolayısıyla keyset imleci ve kümelenmiş anahtar aynı şeydir.
3. **Okunmamış sayısı bedavaya gelir.** `last_seq - last_read_seq` bir çıkarma işlemidir; `messages`
   üzerinde sayım yapan hiçbir sorgu yoktur.
4. **Ekleme sonuna yazar.** Her konuşma kendi aralığının sonuna ekler; tek bir "sıcak son sayfa"
   yoktur, ekleme yükü konuşmalara dağılır.

`id` (UUIDv7) kümelenmiş anahtar **olamaz**: SQL Server `uniqueidentifier`'ı son bayt grubundan
başlayarak sıralar, UUIDv7'nin zaman damgası ise ilk baytlardadır — yani SQL Server açısından
rastgeledir ve her ekleme sayfa bölünmesi olurdu. Bu yüzden `id` kümelenmemiş benzersiz indekstedir;
istemciler ve federasyon için zaman sıralı, motor için maliyetsizdir.

### Boşluksuz `seq` tahsisi

`SqlMessageAppender` tek bir parametreli toplu ifade ve tek bir kısa işlem çalıştırır:

```sql
BEGIN TRANSACTION;
UPDATE dbo.conversations
   SET last_seq = last_seq + 1, last_message_at = @created_at, updated_at = @created_at
OUTPUT inserted.last_seq INTO @alloc (seq)
 WHERE id = @conversation_id;
SELECT @seq = seq FROM @alloc;
INSERT INTO dbo.messages (conversation_id, seq, id, ...) VALUES (@conversation_id, @seq, @new_id, ...);
COMMIT TRANSACTION;
```

- Konuşma satırının **özel (exclusive) satır kilidi** UPDATE ile COMMIT arasında birkaç mikrosaniye
  tutulur; eşzamanlı göndericiler bu kilidin arkasında sıraya girer ve her biri bir sonraki tamsayıyı
  alır. Toplam sıra buradan gelir; koordinatör, `SEQUENCE` nesnesi veya küme bus'ı gerekmez.
- Herhangi bir hata işlemi geri alır, dolayısıyla `last_seq` de geri döner: **taahhüt edilmiş
  numaralar boşluksuzdur.** (`ck_messages_metadata_json` ihlali bunun testidir.)
- Numara INSERT'ten önce bilindiği için göndericiye hemen yankılanabilir.
- Idempotenlik `UQ (sender_id, client_msg_id)` ile sağlanır: yeniden gönderim önce hızlı yolda
  (işlemsiz seek) mevcut satırı bulur; yarışı kaybeden ekleme 2601/2627 alır, geri alınır ve satırı
  yeniden okur. Her iki durumda da aynı `(id, seq)` döner ve `Deduplicated = true` işaretlenir.

Doğrulama: `MessageAppendTests.Sequences_are_gapless_and_unique_under_64_concurrent_appenders`
(64 işçi × 50 ekleme = tam olarak 1..3200, `last_seq = 3200`, LocalDB'de ~0,95 s).

## Neden sıralı GUID (ve neden `Guid.NewGuid()` değil)

SQL Server `uniqueidentifier` değerlerini **son bayt grubundan** başlayarak karşılaştırır. Buna göre:

| Üretici | SQL Server sırası | Kullanım |
|---|---|---|
| `Guid.NewGuid()` | rastgele | Kümelenmiş anahtar olarak **kullanılmaz** |
| `Guid.CreateVersion7()` | rastgele (zaman ilk baytlarda) | Yalnız `messages.id`, kümelenmemiş |
| EF `SequentialGuidValueGenerator` | artan | **Tüm kümelenmiş GUID PK'leri** |

Rastgele bir GUID kümelenmiş indekste rastgele bir sayfaya düşer: her ekleme sayfa bölünmesi
riskidir, indeks parçalanır, tampon havuzu kirlenir. EF'in `SequentialGuidValueGenerator`'ı artan bir
sayacı tam olarak SQL Server'ın sıraladığı baytlara yerleştirir, böylece GUID anahtar bir identity
kolonu gibi indeksin sonuna yazar — kimlik değerini istemci tarafında, gidiş-dönüş olmadan bilmenin
avantajını kaybetmeden.

EF izlenen eklemelerde üreticiyi kendisi uygular (`HasValueGenerator<SequentialGuidValueGenerator>()`).
Ham SQL yazan yollar (iş kuyruğu) aynı üreticiyi `Talky.Data.ValueGeneration.SequentialGuid.Next()`
üzerinden kullanır.

## Neden filtreli indeks

- `IX_messages_parent_id_seq ... WHERE parent_id IS NOT NULL`: mesajların küçük bir azınlığı konu
  yanıtıdır. Filtreli indeks yalnız o satırları taşır — üretimde tam indeksin küçük bir kesri kadar
  yer kaplar, ekleme başına bakım maliyeti yalnız yanıtlarda oluşur ve `parent_id IS NULL` olan
  milyonlarca satır indekse hiç girmez. `INCLUDE (conversation_id)` konu görünümünün anahtar
  aramasını (key lookup) ortadan kaldırır.
- Aynı gerekçe `jobs` tablosunda daha da belirgindir: `WHERE state = 0` filtresi sayesinde talep
  indeksi yalnızca bekleyen işleri içerir, yani bitmiş milyonlarca iş satırı kuyruk sorgusunu
  yavaşlatmaz.
- `UQ ... WHERE <kolon> IS NOT NULL` kalıbı (`users.external_id`, `devices.push_token`,
  `jobs.dedupe_key`) SQL Server'ın "NULL da bir değerdir" benzersizlik davranışını aşmak içindir:
  filtre olmadan ikinci NULL satırı reddedilirdi.

## `messages` ve yabancı anahtarlar

`messages`, `reactions`, `attachments`, `thread_meta`, `calls` ve `call_participants` FK taşımaz.

- `conversation_id`'nin geçerliliği zaten kanıtlanmıştır: sıra numarasını tahsis eden `UPDATE` o satırı
  bulamazsa ekleme hata verir.
- Saklama (retention) mesajları kümelenmiş anahtar boyunca toplu olarak siler; gelen bir FK her
  `DELETE`'i kısıt denetimine çevirir ve şemanın en sıcak tablosunda toplu silmeyi kullanılamaz hale
  getirir.
- Yetim satırlar aynı saklama işi tarafından temizlenir (aşağıya bakın).

Kimlik ve üyelik tablolarında FK vardır, çünkü oradaki hacim küçüktür ve tutarlılık ihlali doğrudan
yetkilendirme hatasıdır.

## `metadata` neden `nvarchar(max)` + `ISJSON`, yerel `json` tipi değil

SQL Server 2025 ve EF Core 10 yerel `json` tipini destekler, ama v0'da kullanılmıyor:

- Yerel `json` **uyumluluk seviyesi 170** ister. Talky'nin desteklediği kurulum senaryolarında
  (mevcut bir AG'ye taşınma, kademeli sürüm yükseltme) veritabanı bir süre daha düşük seviyede
  kalabilir; `nvarchar(max)` her seviyede aynı çalışır.
- `metadata` sunucu için **opaktır**: sıcak yolda hiçbir sorgu içine bakmaz, indekslenmez,
  filtrelenmez. Yerel tipin sunacağı sorgu ve depolama avantajlarının hiçbiri bu kolonda geçerli
  değil.
- `CHECK (metadata IS NULL OR ISJSON(metadata) = 1)` doğrulamanın tamamını, göç riski olmadan verir.
- `ISJSON` maliyeti NULL değerde ihmal edilebilir, ki mesajların çoğunda `metadata` NULL'dır.

Aynı gerekçe `identity_sources.config`, `client_errors.context` ve `audit_logs.data` için de geçerli.
`jobs.payload` bilinçli olarak kısıtsızdır: kuyruk ekleme yolunda her satırda `ISJSON` çalıştırmaya
değmez ve içerik zaten yalnızca üreten kodun kendi işleyicisi tarafından okunur.

Yerel `json`'a geçiş v1.1'de tek bir `ALTER TABLE ... ALTER COLUMN` göçüdür; şema o güne kadar
kilitlenmiş olmaz.

## Adlandırma kuralı

`Talky.Data.Modeling.SnakeCaseNaming.UseSnakeCaseNames()`, `OnModelCreating`'in **en sonunda** çalışan
~40 satırlık bir `ModelBuilder` geçişidir. Yalnız tablo ve kolon adlarını dönüştürür; anahtar, indeks,
FK ve `CHECK` adlarını EF'in kendisi (zaten dönüştürülmüş) tablo ve kolon adlarından türetir, bu yüzden
`PK_messages`, `IX_messages_sender_id_client_msg_id`, `FK_sessions_users_user_id` gibi adlar çıkar.

Üçüncü parti bir adlandırma paketi eklenmedi: ihtiyaç duyulan davranışın tamamı "küçük harf +
alt çizgi" kuralıdır ve kendimiz sahiplenmek hem `talky.exe`'nin bağımlılık yüzeyini küçük tutar hem
kuralı denetlenebilir kılar.

`ToSnakeCase` zaten snake_case olan adlar için **idempotenttir** (`metrics_1m` → `metrics_1m`).
Kural bu yüzden şudur: **bir tabloyu veya kolonu elle adlandırıyorsanız snake_case yazın.**
Şu an elle adlandırılanlar: `metrics_1m`, `jobs.type` (`Job.JobType`), `client_errors.message`
(`ClientError.ErrorMessage`), `users.title` (`User.JobTitle`).

Enumlar için ikinci bir geçiş vardır: `TinyIntEnums.UseTinyIntEnums()` her enum özelliğini `tinyint`e
eşler.

## Enum değer haritaları

Değerler kalıcıdır: **var olan bir değer asla yeniden kullanılmaz veya anlamı değiştirilmez**, yalnız
sona ekleme yapılır (proto sözleşmelerindeki kuralın aynısı).

| Enum | Kolon(lar) | Değerler |
|---|---|---|
| `IdentitySourceKind` | `identity_sources.kind` | 0 Local · 1 ActiveDirectory · 2 Ldap · 3 Oidc · 4 Saml |
| `UserState` | `users.state` | 0 Active · 1 Disabled · 2 Deleted |
| `TeamMemberRole` | `team_members.role` | 0 Member · 1 Owner |
| `ConversationType` | `conversations.type` | 0 Dm · 1 Group · 2 Channel |
| `ConversationRole` | `conversation_members.role` | 0 Member · 1 Admin · 2 Owner |
| `NotifyLevel` | `conversation_members.notify_level` | 0 All · 1 Mentions · 2 None |
| `MessageKind` | `messages.kind` | 0 Text · 1 System · 2 File · 3 Call |
| `ScanStatus` | `attachments.scan_status` | 0 Pending · 1 Clean · 2 Infected · 3 Failed · 4 Skipped |
| `CallKind` | `calls.kind` | 0 Direct · 1 Group · 2 Meeting |
| `CallState` | `calls.state` | 0 Ringing · 1 Active · 2 Ended · 3 Missed · 4 Declined · 5 Failed |
| `CallParticipantState` | `call_participants.state` | 0 Invited · 1 Ringing · 2 Joined · 3 Left · 4 Declined · 5 Missed |
| `DevicePlatform` | `devices.platform`, `client_errors.platform` | 0 Unknown · 1 Web · 2 Windows · 3 MacOs · 4 Linux · 5 Android · 6 Ios |
| `PushKind` | `devices.push_kind` | 0 None · 1 Fcm · 2 Apns · 3 WebPush |
| `JobState` | `jobs.state` | 0 Pending · 1 Running · 2 Succeeded · 3 Dead · 4 Cancelled |
| `FederationPeerState` | `federation_peers.state` | 0 Pending · 1 Active · 2 Suspended · 3 Revoked |

`cluster_nodes.roles` enum değil, `Talky.Core.Hosting.NodeRole` bit maskesidir (`int`):
1 Api · 2 Worker · 4 Search · 8 PushRelay.

`audit_logs.action` bilinçli olarak `nvarchar(64)` metindir (`user.disable`, `team.create`,
`session.revoke`): denetim fiilleri sık eklenir ve bir enum için göç gerektirmesi gereksiz sürtünmedir.

## Saklama (retention)

Saklama, kümelenmiş anahtar boyunca **toplu** silmedir; asla tek büyük `DELETE` değil. Her toplu iş
kısa bir işlemdir, böylece log büyümesi ve kilit yükselmesi sınırlı kalır.

```sql
-- Konuşma başına, en eski mesajlardan başlayarak. seq kümelenmiş anahtarın ikinci kolonu
-- olduğu için bu bir aralık silmesidir, tarama değil.
DELETE TOP (5000) FROM dbo.messages
 WHERE conversation_id = @conversation_id AND seq < @keep_from;
```

Döngü `@@ROWCOUNT < 5000` olana kadar tekrarlanır. `@keep_from`, saklama politikasından hesaplanır
(örneğin "son 2 yıl" → o tarihten önceki en yüksek `seq`).

Kurallar:

- `conversations.last_seq` **asla** düşürülmez. Silinen aralık geride boşluk bırakır, bu beklenendir:
  boşluksuzluk garantisi *tahsis* içindir, *sonsuza kadar saklama* için değil. İstemciler eksik
  aralığı `ResyncRequired` ile öğrenir.
- `messages` silindikten sonra aynı iş yetimleri temizler (FK olmadığı için otomatik değildir):
  `reactions`, `attachments`, `thread_meta` için `message_id`/`root_message_id` artık var olmayan
  satırlar.
- `attachments` silinmeden önce blob dosya deposundan kaldırılır; sıra tersine olursa yetim blob kalır.
- Diğer tablolar zaman kolonlarının indeksi boyunca aynı `DELETE TOP (n)` kalıbıyla süpürülür:
  `sessions` (`expires_at`), `jobs` (`completed_at`), `metrics_1m` (`bucket`),
  `client_errors` (`received_at`), `calls` (`started_at`).
- `audit_logs` yalnız açık bir saklama politikasıyla ve ayrı bir onayla süpürülür; varsayılan
  davranış hiç silmemektir.

Saklama işleri M2'de `jobs` kuyruğunda çalışacaktır; bu belge kalıbı v0'da sabitler.

## Yapılandırma ve DI

`Talky:Database` bölümü (`DatabaseOptions`, `ValidateDataAnnotations` + `ValidateOnStart`):

| Anahtar | Varsayılan | Anlamı |
|---|---|---|
| `Talky:Database:ConnectionString` | — (**zorunlu**) | SQL Server bağlantı dizesi. Varsayılanı yoktur: üretimde sessizce yerel bir örneğe düşmek, başlamamaktan kötüdür. |
| `Talky:Database:CommandTimeoutSeconds` | `5` | Her sorgunun zaman aşımı. Bilinçli olarak kısa: sıcak yolda saniyelerce süren sorgu beklenecek bir şey değil, bir arızadır. |
| `Talky:Database:MaxRetryCount` | `3` | Geçici hatalar (AG yük devretmesi, kilitlenme kurbanı, zaman aşımı) için yeniden deneme. `0` stratejiyi kapatır. |
| `Talky:Database:MigrateOnStartup` | `false` | Açıksa host dinlemeye başlamadan önce göçleri uygular. Üretimde kapalı: yükseltme `talky migrate` ile açık ve denetlenebilir bir adımdır. |

`services.AddTalkyData(builder.Configuration)` şunları kaydeder:

| Servis | Ömür | Not |
|---|---|---|
| `TalkyDbContext` | havuzlanmış scoped | `AddDbContextPool`, `NoTracking`, `EnableRetryOnFailure`, `CommandTimeout` |
| `IMigrationRunner` → `EfMigrationRunner` | scoped | |
| `IMessageReader` → `EfMessageReader` | scoped | derlenmiş keyset sorgusu |
| `IMessageAppender` → `SqlMessageAppender` | singleton | |
| `IJobQueue` → `SqlJobQueue` | singleton | |
| `IClusterNodeRegistry` → `SqlClusterNodeRegistry` | singleton | |
| `IDoctorCheck` → `SqlDoctorCheck` | singleton (enumerable) | `talky doctor` kontrolü, adı `sql` |
| `SqlConnectionSource` | singleton | EF dışı yolların ADO.NET bağlantıları |
| `TimeProvider` | singleton (`TryAdd`) | testler kendi sağlayıcısını önceden kaydedebilir |
| Sağlık kontrolü `sql` | — | `AddHealthChecks().AddCheck<SqlHealthCheck>("sql", tags: ["ready"])`, `SELECT 1`, 2 sn bütçe |

Host tarafında yapılması gerekenler: `AddTalkyData(builder.Configuration)` çağrısı, hazır olma uç
noktasının `ready` etiketiyle eşleştirilmesi (`app.MapHealthChecks("/readyz", new() { Predicate = r
=> r.Tags.Contains("ready") })`), `talky migrate` komutunun `IMigrationRunner`'a bağlanması ve
`MigrateOnStartup` true ise başlangıçta `MigrateAsync` çağrılması.

## Göçler

### Yeni göç ekleme

```bash
cd server
dotnet ef migrations add <Ad> --project src/Talky.Data --startup-project src/Talky.Data
dotnet build Talky.slnx -m:1 -p:NodeReuse=false     # üretilen kod derlenmeli
dotnet ef migrations script --project src/Talky.Data --startup-project src/Talky.Data
```

Tasarım zamanı bağlantı dizesi `TalkyDbContextFactory`'den gelir: `TALKY_DB` ortam değişkeni, yoksa
`Server=(localdb)\MSSQLLocalDB;Database=talky_dev;Trusted_Connection=True;TrustServerCertificate=True;Encrypt=False`.

Boş bir veritabanına uygulayıp doğrulamak için:

```bash
TALKY_DB='Server=(localdb)\MSSQLLocalDB;Database=talky_scratch;Trusted_Connection=True;TrustServerCertificate=True;Encrypt=False' \
  dotnet ef database update --project src/Talky.Data --startup-project src/Talky.Data
```

`src/Talky.Data/Migrations/.editorconfig` bu klasördeki dosyaları üretilmiş kod sayar; aksi halde
`TreatWarningsAsErrors` iskele kodunu derleme hatasına çevirirdi.

### `Init` göçünde elle eklenen iki adım

Üretilen dosyada iki ifade elle eklenmiştir ve `docs/data-model.md` bunların kayıt yeridir:

1. **RCSI**, `Up()`'ın başında, `suppressTransaction: true` ile:
   `ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;`
   İşlem içinde çalışamaz ve veritabanındaki tek bağlantı olmak ister — göç koşucusunun uygulama
   kilidini `master`'da alma sebebi budur.
2. **Sayfa sıkıştırma**, `Up()`'ın sonunda, indeksler oluştuktan sonra:
   `ALTER INDEX ALL ON [messages] REBUILD WITH (DATA_COMPRESSION = PAGE);`
   `messages` açık ara en büyük tablodur ve satırları sayfa içinde son derece tekrarlıdır (aynı
   `conversation_id`, az sayıda farklı `sender_id` ve `kind`, birbirine yakın zaman damgaları), bu
   yüzden sayfa sıkıştırma tipik olarak tabloyu yarıya indirir: daha az sayfa = daha az mantıksal
   okuma ve gigabayt başına daha çok geçmiş. Karşılığında ekleme başına biraz CPU harcanır, ki
   ekleme yolu bunu kaldırır.

> **Dikkat:** İleride `messages` tablosunu yeniden oluşturan bir göç yazılırsa `ALTER INDEX ALL ...
> DATA_COMPRESSION = PAGE` ifadesini o göçün sonunda **tekrar** çalıştırın; aksi halde sıkıştırma
> sessizce kaybolur. `MigrationTests.Messages_is_page_compressed_and_clustered_on_conversation_and_seq`
> bunu yakalar.

### Göçlerin uygulanması (çalışma zamanı)

`EfMigrationRunner`:

1. Bağlantı dizesinin bir kopyasıyla `master`'a bağlanır ve
   `sp_getapplock @Resource = 'talky-migrate:<veritabanı adı>', @LockMode = 'Exclusive',
   @LockOwner = 'Session', @LockTimeout = 60000` alır. `Session` sahipliği sayesinde süreç öldürülse
   bile kilit bağlantı kapanınca serbest kalır. Kaynak adı veritabanı adını taşır, böylece aynı örnek
   üzerindeki iki Talky kurulumu (veya iki test veritabanı) birbirini beklemez.
2. Veritabanı yoksa oluşturur (`RelationalDatabaseCreator`).
3. Bekleyen göç yoksa hiçbir şey yapmaz; varsa `Database.MigrateAsync()` çağırır.
4. Kilidi bırakır.

Kademeli yükseltmede aynı anda başlayan ikinci düğüm en fazla 60 saniye bekler ve ardından bekleyen
göç bulamaz.

## Testler

```bash
cd server
dotnet test tests/Talky.UnitTests -m:1 -p:NodeReuse=false          # 43 test, veritabanı gerekmez
dotnet test tests/Talky.IntegrationTests -m:1 -p:NodeReuse=false   # 25 test, LocalDB
```

Entegrasyon testleri her test sınıfı için `talky_test_<8 hex>` adında bir veritabanı oluşturur,
gerçek `IMigrationRunner` ile göçleri uygular ve sonunda düşürür.

- `TALKY_TEST_DB_SERVER` sunucuyu değiştirir (varsayılan `(localdb)\MSSQLLocalDB`).
- `TALKY_SKIP_DB_TESTS=1` veritabanı gerektiren tüm testleri atlar.
