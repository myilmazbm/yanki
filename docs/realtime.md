# Talky.Realtime — WebSocket çekirdeği (M0)

`Talky.Realtime`, `talky.exe`'nin gerçek zamanlı ön kapısıdır: bir düğümde 10–15 bin WebSocket
bağlantısını taşır, `docs/protocol.md`'deki v1 el sıkışmasını uygular, geri basınç uygular ve
dolamalı (rolling) yükseltmede bağlantıları düzgünce boşaltır.

Tel formatı ve çerçeve anlamları **`docs/protocol.md`** ile `api/proto/talky/v1/frames.proto`
tarafından tanımlanır; burada yalnızca sunucu tarafı iç işleyiş, ayar düğmeleri ve ölçümler yer alır.

## Ana hattı bağlama

```csharp
builder.Services.AddTalkyRealtime(builder.Configuration);   // Talky:Realtime bölümünü bağlar

var app = builder.Build();
app.UseWebSockets();          // ZORUNLU ve endpoint middleware'inden önce olmalı
app.MapTalkyRealtime();       // Talky:Realtime:Path üzerine haritalar, varsayılan /ws
```

- `AddTalkyRealtime(IServiceCollection, IConfiguration)` — seçenekleri bağlar ve
  `ValidateDataAnnotations().ValidateOnStart()` ile açılışta doğrular. Test ve sabitleme için
  `AddTalkyRealtime(services, configuration, Action<RealtimeOptions>)` aşırı yüklemesi vardır.
- `MapTalkyRealtime(IEndpointRouteBuilder)` — `IEndpointConventionBuilder` döndürür, böylece
  `.AllowAnonymous()`, `.RequireHost(...)` gibi kurallar zincirlenebilir. Endpoint adı
  `TalkyRealtime`.
- `UseWebSockets()` çağrılmazsa her istek 400 ile yanıtlanır ve tek seferlik bir uyarı loglanır.
- Kimlik doğrulama HTTP değil **`Hello` çerçevesi** üzerinden yapılır. Ana hatta genel bir
  authorization fallback politikası varsa endpoint'te `.AllowAnonymous()` çağırın.
- Gerçek bir `ISessionAuthenticator` kayıtlı değilse ve geliştirme jetonları kapalıysa modül
  **kapalı-güvenli** (fail-closed) bir doğrulayıcı kaydeder: düğüm açılır, açılışta tek bir uyarı
  (olay 1017) loglanır ve her `Hello` `Error{UNAUTHENTICATED}` + 1008 ile yanıtlanır. Talky.Identity
  (M1) kendi doğrulayıcısını düz `AddSingleton` ile kaydeder; `AddTalkyRealtime`'dan önce kaydederse
  `TryAdd` hiçbir şey yapmaz, sonra kaydederse son kayıt olarak o çözülür — sıra fark etmez.

Modülün ana hattan beklediği kayıtlar: `INodeInfo`, `IDrainCoordinator` ve (M1'den itibaren)
gerçek bir `ISessionAuthenticator`. `TimeProvider` kayıtlı değilse `TimeProvider.System` eklenir.

## Bağlantı yaşam döngüsü

```
  HTTP GET /ws (Upgrade)
        │
        ├─ düğüm drain ediyorsa → yalnızca kapatma 1013 (Reconnect çerçevesi yok)
        │
        ▼
  AcceptWebSocketAsync(KeepAliveInterval, KeepAliveTimeout)
        │  ClientConnection (UUIDv7 id) oluşturulur, registry'ye eklenir,
        │  yazıcı döngüsü (writer loop) başlatılır
        ▼
  okuma döngüsü (istek thread'i)          yazıcı döngüsü (ayrı Task)
        │                                      │
        │ Hello ────► kimlik doğrulama ────────┤ Welcome
        │                                      │
        │ Ping  ────────────────────────────── ┤ Pong
        │ Hello (ikinci) ────────────────────  ┤ Error{ALREADY_AUTHENTICATED} (açık kalır)
        │ Bye   ──────────────────────────────►│ kapatma 1000
        │ bozuk/ikili çerçeve ────────────────►│ Error{INVALID_FRAME} + 1003
        │ büyük çerçeve ──────────────────────►│ Error{INVALID_FRAME} + 1009
        ▼                                      ▼
  registry'den çıkarılır ◄──────────── kuyruk boşaltılır, CloseOutputAsync
```

Bir WebSocket aynı anda **bir gönderim ve bir alım** destekler; bu yüzden okuma döngüsü istek
thread'inde, yazma döngüsü ayrı bir `Task`'ta çalışır ve ikisi çakışmaz.

Kapatma **idempotenttir**: `RequestClose(status, description)` ilk çağrıda kazanır, sonrakiler yok
sayılır. Böylece drain, bağlantı bütçesi tahliyesi ve protokol hatası aynı anda tetiklense bile
istemci tek ve doğru bir kapatma nedeni görür. `RequestClose` kuyruğu tamamlar; yazıcı döngüsü
bekleyen çerçeveleri gönderdikten **sonra** kapatma çerçevesini yollar — yani hata çerçevesi her
zaman kapatmadan önce ulaşır.

### Alım tamponları

Bağlantı başına `ArrayPool`'dan 4 KiB'lik tek bir tampon kiralanır. v1'in tüm kontrol çerçeveleri
buna sığar, yani sık yol tek `ReceiveAsync` + sıfır ek kiralamadır. Parçalı (fragmented) veya daha
büyük mesajlar geçici olarak `MaxFrameBytes + 1` boyutlu havuz tamponuna taşar ve hemen iade edilir.
Bağlantı başına 64 KiB ayırmak 15 bin bağlantıda ~1 GB olurdu; bu yüzden büyük tampon asla kalıcı
değildir.

## Canlılık (heartbeat)

`docs/protocol.md`'deki iki yönlü model birebir uygulanır:

| Yön | Mekanizma | Uygulama |
|---|---|---|
| Sunucu → istemci ölü mü? | RFC 6455 protokol ping/pong | `WebSocketAcceptContext.KeepAliveInterval = HeartbeatInterval`, `KeepAliveTimeout = 2 × HeartbeatInterval` (.NET 9+). Taşıma katmanı ölü eşi kendi kapatır; uygulama kodu çalışmaz, tarayıcı istemcisi hiçbir şey yazmaz. |
| İstemci → sunucu ölü mü? | Uygulama `Ping`/`Pong` | İstemci `Welcome.heartbeatIntervalMs` aralığıyla `Ping{ts}` yollar; sunucu `Pong{ts, serverTime}` ile `ts`'i **birebir** geri döndürür. `PongTimeoutMs` yalnızca istemci ipucudur, sunucu onu uygulamaz. |

**Bağlantı başına zamanlayıcı yoktur.** Tüm zamana bağlı işleri tek bir `PeriodicTimer` süpürmesi
yapar (`RealtimeSweepService`):

- `Hello` göndermeyen bağlantıları `Error{HELLO_REQUIRED}` + 1008 ile kapatır;
- kapatma isteği üzerinden `CloseGrace` (2 sn) geçmiş, hâlâ kapanmamış soketleri `Abort()` eder.

Süpürme aralığı `min(HelloTimeoutMs, 5 sn)`'dir: varsayılanda tam 5 saniye, testler veya sıkı bir
`HelloTimeoutMs` yapılandırıldığında tespit gecikmesi hiçbir zaman zaman aşımının iki katını geçmez.

## Geri basınç (backpressure)

Her bağlantının kapasitesi `OutboundQueueCapacity` olan sınırlı bir `Channel<ServerFrame>` kuyruğu
ve onu boşaltan tek bir yazıcı döngüsü vardır.

- `Enqueue(frame, ephemeral: false)` **asla bloke etmez ve asla istisna atmaz**; kuyruğa
  girildiyse `true` döner.
- Kuyruk doluysa: kuyruğun tamamı atılır ve yerine tek bir `ResyncRequired{full:true}` konur.
  Gerekçe: kuyruk dolduysa içindeki her çerçeve sunucunun mevcut durumuna göre zaten eskimiştir;
  istemciyi `GET /api/v1/sync` ile yeniden senkronize etmek, eskimiş bir kuyruğu yavaşça
  boşaltmaktan hem daha hızlı hem daha doğrudur. Üretici tarafı bundan etkilenmez.
- `Enqueue(frame, ephemeral: true)` — yazarken/çevrimiçi gibi geciktiğinde değerini yitiren
  çerçeveler içindir. Kuyruk **%75** doluluğa ulaştığında bunlar önce düşürülür, böylece kalıcı
  çerçeveler için yer kalır.
- Düşen her çerçeve `talky.realtime.frames{direction="dropped"}` ile sayılır ve taşma bir uyarı
  logu üretir.

`Channel` `BoundedChannelFullMode.Wait` ile kurulur; amaç beklemek değil, `TryWrite`'ın taşmayı
sessizce yutmak yerine `false` döndürmesidir. Politika tek bir yerde, `HandleOverflow` içindedir.

## Drain (dolamalı yükseltme)

`RealtimeDrainService`, `IDrainCoordinator.DrainToken`'ı izler. Token iptal edildiğinde:

1. Her bağlantıya **aynı üst sınırla** `Reconnect{delayMs, reason:"draining"}` yollanır:
   `delayMs = min(10 sn, grace)`; `grace` olarak `HostOptions.ShutdownTimeout` okunur. Rastgele
   gecikmeyi **istemci** `[0, delayMs]` aralığında çeker — jitter yalnızca bir tarafta uygulanır.
   Amaç 15 bin istemcinin aynı milisaniyede bir sonraki düğüme yığılmasını engellemektir.
2. `CloseGrace` (2 sn) sonra tüm bağlantılar **1001** ile kapatılır.
3. Drain sırasında gelen **yeni** bağlantılar `Reconnect` çerçevesi **olmadan** yalnızca **1013**
   (“try again later”) ile kapatılır; istemci normal üstel geri çekilmesini uygular. İstemci
   `Reconnect` gecikmesine yalnızca bağlantı `open` durumundayken uyar.

## Kullanıcı başına bağlantı bütçesi

`MaxConnectionsPerUser` (varsayılan 16) aşıldığında o kullanıcının **en eski** bağlantıları
tahliye edilir: önce `Reconnect{reason:"too_many_connections"}`, ardından **1001**. Sıralama
`ConnectedAt`, eşitlikte bağlantı id'si ile belirlenir; kapanmakta olan bağlantılar aday değildir,
bu yüzden tekrar tekrar çağrılması güvenlidir.

> **İstemcinin uyması gereken kural:** `reason` alanı sözleşmenin parçasıdır.
> `"draining"` → `[0, delayMs]` aralığında rastgele bekleyip yeniden bağlan.
> `"too_many_connections"` → **otomatik yeniden bağlanma.** Aynı kullanıcının daha yeni bir
> bağlantısı zaten canlıdır; yeniden bağlanmak onu tahliye eder ve iki cihaz birbirini sonsuza
> kadar atar. Bunu “başka bir cihazda oturum açıldı” olarak gösterin.

## Ayar düğmeleri (`Talky:Realtime`)

| Anahtar | Varsayılan | Anlamı |
|---|---|---|
| `Path` | `/ws` | Endpoint yolu; `/` ile başlamalı. |
| `HeartbeatIntervalMs` | `25000` | `Welcome`'da duyurulan aralık **ve** protokol keep-alive ping aralığı. Keep-alive zaman aşımı bunun 2 katıdır. |
| `HelloTimeoutMs` | `5000` | `Hello` için son tarih. Süpürme aralığını da belirler (`min(bu, 5 sn)`). |
| `PongTimeoutMs` | `10000` | Yalnızca istemci ipucu; sunucu uygulamaz. |
| `MaxFrameBytes` | `65536` | Kabul edilen en büyük gelen çerçeve; aşılırsa 1009. |
| `OutboundQueueCapacity` | `256` | Bağlantı başına giden kuyruk. %75'i geçici çerçeve eşiğidir. |
| `AllowDevTokens` | `false` | `dev:<kullanıcı>` jetonlarını kabul eden geliştirme kimlik doğrulayıcısını kaydeder. **Üretimde asla açmayın.** Açıkken açılışta uyarı loglanır; `Development` ortamı dışında ana hat (`talky.exe`) bunu açılışta reddeder, `Talky:Server:AllowInsecureHttp=true` olan laboratuvar düğümleri hariç. Kapalıyken ve gerçek doğrulayıcı yokken her `Hello` reddedilir. |
| `MaxConnectionsPerUser` | `16` | Kullanıcı başına eşzamanlı bağlantı tavanı. |

Değerler DataAnnotations ile sınırlıdır ve `ValidateOnStart` sayesinde hatalı bir yapılandırma
süreç açılışında, ilk istekte değil, patlar.

### Geliştirme jetonları

`AllowDevTokens=true` iken `DevTokenSessionAuthenticator` yalnızca **başka bir
`ISessionAuthenticator` kayıtlı değilse** (`TryAdd`) devreye girer. `dev:<guid>` jetonu o guid'i,
`dev:<isim>` ise isimden türetilmiş **kararlı** bir kullanıcı id'sini verir (SHA-256, RFC 9562
sürüm 8 UUID). Kararlılık önemlidir: `dev:alice` her yeniden bağlanmada, her süreçte, her düğümde
aynı kullanıcıdır — çok cihazlı davranış ve bağlantı bütçesi veritabanı olmadan test edilebilir.

## Ölçümler ve loglar

`Meter("Talky.Realtime")`:

| Enstrüman | Tür | Etiketler |
|---|---|---|
| `talky.realtime.connections` | UpDownCounter | — |
| `talky.realtime.frames` | Counter | `direction` = `in` \| `out` \| `dropped`, `type` = çerçeve gövdesi |
| `talky.realtime.errors` | Counter | `code` = `ErrorCode` |
| `talky.realtime.handshake_duration` | Histogram (ms) | — |

Loglar `[LoggerMessage]` ile üretilir, İngilizcedir, olay id'leri 1000–1016 aralığındadır ve
**hiçbir zaman jeton ya da kullanıcı içeriği taşımaz** (kimliği doğrulanamayan bağlantıda bile
yalnızca “token was rejected” yazılır).

## Altın fixture'lar

`api/fixtures/ws/*.json` biçimi `docs/protocol.md`'de tanımlıdır. Sunucu tarafı sözleşme testleri
(`Talky.ContractTests`) `c2s` çerçevelerini birebir gönderir, `s2c` çerçevelerini **yapısal olarak**
doğrular: beklentide listelenen alanlar eşleşmeli, gerçek çerçeve fazladan alan taşıyabilir (ileri
uyumluluk) ve `"…"` yalnızca varlık kontrolüdür.

Bu depo biçime iki **isteğe bağlı, geriye dönük uyumlu** ekleme yapar; bilmeyen bir oynatıcı
ikisini de yok sayabilir:

- üst düzey `"close": { "code": 1008 }` — listelenen çerçevelerden sonra sunucunun göndereceği
  kapatma kodu;
- `{ "dir": "server-action", "action": "drain" }` — sıralı bir girdi olarak sunucu tarafı bir olayı
  tetikler. Hiçbir istemci çerçevesi drain üretemediği için `reconnect_on_drain.json` bunsuz
  yazılamaz. v0'da tanımlı tek eylem `drain`'dir.

M0 akışları: `handshake_ok`, `ping_pong`, `bad_version`, `unauthenticated`, `hello_required`,
`already_authenticated`, `bye`, `reconnect_on_drain`.

## Yük aracı: `talky-wsload`

```bash
cd server
dotnet build tools/Talky.WsLoad/Talky.WsLoad.csproj -c Release

dotnet tools/Talky.WsLoad/bin/Release/net10.0/talky-wsload.dll \
  --url wss://talky.local/ws \
  --connections 1000 --ramp 100/s --duration 30s \
  --token "dev:u{i}" --ping-interval 1s [--insecure] [--target-pid <sunucu-pid>]
```

- `--token` içindeki `{i}` bağlantı sırası ile değiştirilir. Bu **önemlidir**: `{i}` içermeyen bir
  jetonla 1000 bağlantı açarsanız hepsi tek kullanıcıya düşer ve `MaxConnectionsPerUser` en
  eskileri tahliye eder. Araç bu durumda uyarır.
- `--ping-interval` gerçek istemcilerden hızlıdır (varsayılan 1 sn), amacı kullanılabilir bir RTT
  dağılımı toplamaktır. Boşta bellek ölçmek için uzun bir değer verin (örn. `300s`).
- `--target-pid` verilirse aracın kendi RSS'inin yanında sunucu sürecinin çalışma kümesi de
  örneklenir; rapor ilk bağlantıdan **önceki** taban değeri çıkararak bağlantı başına bellek verir.
- Çıkış kodu: bağlantı başarısı %99'un altındaysa 1.
- Kapanış her zaman `Bye` + kapatma el sıkışmasıyladır.

### Ölçülen değerler (M0, geliştirme makinesi, Windows 11, loopback)

`RealtimeBench` (yalnız `Talky.Realtime` + Kestrel) karşısında, Release:

| Senaryo | Sonuç |
|---|---|
| 1000 bağlantı, ramp 100/s, 30 sn, ping 1 sn | %100 bağlantı, el sıkışma p50 **9,2 ms** / p99 **277 ms**, ping RTT p50 **0,73 ms** / p99 **6,8 ms**, ~1780 çerçeve/sn |
| 12 000 boşta bağlantı, ramp 500/s | %100 bağlantı, **27,3 KiB/bağlantı** (boştaki tabana göre artış) |
| 9 024 boşta bağlantı (taze süreç) | çalışma kümesi 61,4 → 329,1 MB ⇒ **~30,4 KiB/bağlantı**; zorlanmış GC sonrası yönetilen yığın 1,1 → 185,0 MB ⇒ **~20,9 KiB/bağlantı** |

Hedef bağlantı başına ~40 KB idi; ölçülen ~27–31 KB. Not: 2000/s gibi agresif bir ramp'te loopback
üzerinde istemci tarafı bağlantı hataları görülür (geçici port/backlog), 500–1000/s'te %100.

## M2'de ne değişecek

M0 yalnızca oturum katmanıdır; taşınan tek uygulama trafiği `Ping`/`Pong`'dur. M2'de eklenecekler:

- **Fan-out.** Konuşma üyelerine olay dağıtımı. `IConnectionRegistry.GetByUser` ve
  `BroadcastAsync` bunun için hazır; sıcak yol `Snapshot()` yerine kullanıcıya göre indeks
  kullanacak ve tek bir `ServerFrame` nesnesi tüm alıcılarda paylaşılacak (çerçeveler değişmezdir).
- **`seq` ve resume.** `Hello.since` haritası bugün okunur ama kullanılmaz. M2'de bağlantı,
  konuşma başına son `seq`'i tutacak; boşluk kapatılabiliyorsa SQL'den tekrar oynatılacak,
  kapatılamıyorsa bugünkü `ResyncRequired` yolu kullanılacak.
- **Geçici çerçeveler.** `Enqueue(..., ephemeral: true)` yazıyor/çevrimiçi göstergeleri için
  kullanılacak; politika (%75 eşiği) bugünden yerinde.
- **Küme.** Başka bir düğümdeki alıcılar `talky.cluster.v1` mesh'i üzerinden bulunacak; doğruluk
  yine bus'a değil SQL'deki `seq`'e ve istemci resync'ine dayanacak.
- **Hız sınırlama.** `Error{RATE_LIMITED, retryAfterMs}` bugün üretilmiyor; kullanıcı/bağlantı
  başına kota M2 ile gelecek.

Kablo biçiminde kırıcı değişiklik beklenmiyor: `ClientFrame`/`ServerFrame` `oneof` gövdeleri yalnız
eklemeli büyür ve bilinmeyen alanlar bugün de yok sayılır.

## Testler

```bash
cd server
dotnet test tests/Talky.UnitTests/Talky.UnitTests.csproj -m:1 -p:NodeReuse=false
dotnet test tests/Talky.IntegrationTests/Talky.IntegrationTests.csproj -m:1 -p:NodeReuse=false
dotnet test tests/Talky.ContractTests/Talky.ContractTests.csproj -m:1 -p:NodeReuse=false
```

Entegrasyon ve sözleşme testleri `tests/Talky.IntegrationTests/Realtime/Harness` altındaki ortak
test ana hattını kullanır: `WebApplication.CreateBuilder` + `UseTestServer`, `INodeInfo`,
`IDrainCoordinator` ve `ISessionAuthenticator` için test ikizleri. `Talky.Server`'a bağımlı
değildir, böylece gerçek zamanlı modül tek başına doğrulanabilir.
