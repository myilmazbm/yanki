# Talky gerçek zamanlı protokolü (v1)

Tek gerçek kaynak: `api/proto/talky/v1/frames.proto`. Bu belge proto'da ifade edilemeyen
davranış kurallarını tanımlar. Sunucu (`Talky.Realtime`) ve istemci (`@talky/core`) bu kurallara
uyar; `api/fixtures/ws/*.json` altın akışları her iki tarafın testlerinde çalıştırılır.

## Uç nokta

- Uç nokta `/ws`'dir (`Talky:Realtime:Path`), sorgu parametresi yoktur; protokol sürümü `Hello.protoVersion` ile
  taşınır. HTTP/1.1 GET upgrade ve HTTP/2 extended CONNECT (RFC 8441) kabul edilir.
- Kimlik doğrulama bugün yalnızca `Hello.token` üzerindendir. Çerez ile kimliği doğrulanan web istemcileri
  `Hello.token`'ı boş bırakacaksa (frames.proto yorumu), bu M1'de bir Origin izin listesi ve HttpContext'e erişen bir
  doğrulayıcı gerektirir: sunucu çerez değerini token olarak doğrular ve `Origin`, `Talky:Server:PublicUrl` veya
  yapılandırılmış izin listesiyle eşleşmiyorsa upgrade'i **reddetmelidir**.

## Kodlama

- Bir WebSocket **text** çerçevesi = bir proto3-JSON nesnesi (`ClientFrame` veya `ServerFrame`).
- `oneof body` alan adı çerçeve tipidir: `{"rid":"…","hello":{…}}`, `{"id":"…","welcome":{…}}`.
- Alan adları proto3 JSON'un varsayılan lowerCamelCase biçimindedir (`protoVersion`, `heartbeatIntervalMs`).
- Bilinmeyen alanlar yok sayılır (ileri uyumluluk).
- Bilinmeyen veya boş `oneof` gövdesi: `Welcome`'dan önce her Hello-dışı çerçeve gibi `Error{HELLO_REQUIRED}` + 1008;
  `Welcome`'dan sonra `Error{INVALID_FRAME, rid}` ve bağlantı **açık kalır** (eski düğümde yeni istemci için ileri
  uyumluluk). Ayrıştırılamayan JSON ve binary çerçeve `Error{INVALID_FRAME}` + 1003.
- Azami çerçeve boyutu varsayılan 64 KiB (`Talky:Realtime:MaxFrameBytes`); aşılırsa `Error{INVALID_FRAME}` ve kapatma kodu 1009.

## El sıkışma

1. İstemci bağlanır ve **5 sn** (`Talky:Realtime:HelloTimeoutMs`) içinde `Hello` gönderir; aksi halde sunucu
   `Error{HELLO_REQUIRED}` gönderip 1008 ile kapatır. Süre yalnızca `Hello` çerçevesinin alınmasını kapsar; `Hello`
   alındıktan sonra kimlik doğrulama sürerken bağlantı kapatılmaz. Bu bir zamanlama hatasıdır, kimlik hatası değil
   (askıya alınmış mobil uygulama, donmuş sekme): `Error{HELLO_REQUIRED}` + 1008 alan istemci normal backoff ile
   yeniden bağlanır.
2. `protoVersion != 1` → `Error{PROTOCOL_VERSION_UNSUPPORTED}`, kapatma 1002.
3. Token doğrulanamazsa → `Error{UNAUTHENTICATED}`, kapatma 1008. Gerçek bir doğrulayıcı yokken (M1 öncesi, geliştirme
   jetonları kapalı) düğüm açılır ve her `Hello` `Error{UNAUTHENTICATED}` + 1008 alır. `Talky:Realtime:AllowDevTokens=true`
   Development dışında, `Talky:Server:AllowInsecureHttp=true` değilse başlangıçta reddedilir.
4. Başarı → `Welcome`. `Hello`'dan sonra ikinci bir `Hello` → `Error{ALREADY_AUTHENTICATED}` (bağlantı açık kalır).
5. `Welcome`'dan önce gelen diğer tüm çerçeveler → `Error{HELLO_REQUIRED}`, kapatma 1008.

## Canlılık (heartbeat) modeli

İki ayrı mekanizma vardır; ikisi de gereklidir:

| Yön | Mekanizma | Neden |
|---|---|---|
| Sunucu → istemci ölü mü? | **WebSocket protokol ping/pong** (RFC 6455 kontrol çerçeveleri). Sunucu `KeepAliveInterval` (= `heartbeatIntervalMs`, varsayılan 25 s) ile ping atar, `KeepAliveTimeout` (2×) içinde pong gelmezse kapatır. | Tarayıcılar protokol ping'ine otomatik pong verir; istemci kodu yazmaz. |
| İstemci → sunucu ölü mü? | **Uygulama seviyesi `Ping`/`Pong` çerçeveleri.** İstemci `Welcome.heartbeatIntervalMs` aralığıyla `Ping{ts}` gönderir; `Pong` 10 s içinde gelmezse bağlantıyı ölü sayar ve yeniden bağlanır. | JS, protokol ping'ini göremez; ölü sunucuyu ancak uygulama çerçevesiyle fark eder. |

`Pong.ts` istemcinin gönderdiği değeri aynen döndürür; RTT ölçümü buradan yapılır.
İstemci `Ping`'i her `heartbeatIntervalMs`'de **her zaman** gönderir. Kurulu bir pong son tarihi sonraki Ping'lerle yeniden kurulmaz.

## Kapatma kodları ve `Reconnect.reason`

Sunucunun kullandığı WebSocket kapatma kodları ve istemcinin zorunlu davranışı:

| Kod | Sunucu ne zaman gönderir | İstemci |
|---|---|---|
| 1000 | İstemcinin `Bye`'ına veya istemcinin başlattığı kapatmaya yanıt | `closed`; yeniden bağlanmaz |
| 1001 | Drain (`Reconnect{reason:"draining"}` sonrası), düşürme (`Reconnect{reason:"too_many_connections"}` sonrası) veya bağlantıda beklenmeyen sunucu hatası (çerçevesiz) | `Reconnect` alındıysa `reason`'a göre (aşağıya bakın), yoksa backoff ile yeniden bağlan |
| 1002 | `PROTOCOL_VERSION_UNSUPPORTED` | kalıcı; `closed` |
| 1003 | Ayrıştırılamayan JSON, binary çerçeve | kalıcı; `closed` (istemci hatası) |
| 1008 | `HELLO_REQUIRED` (Welcome öncesi Hello-dışı çerçeve veya Hello zaman aşımı), `UNAUTHENTICATED` | `Error{HELLO_REQUIRED}` sonrası: backoff ile yeniden bağlan (Hello geç ulaştı). `Error{UNAUTHENTICATED}` sonrası veya öncesinde `Error` çerçevesi yoksa: kalıcı; `closed` (istemci `retryOnAuthFailure` ile yeniden deneyebilir) |
| 1009 | Çerçeve `MaxFrameBytes`'ı aştı (`Error{INVALID_FRAME}` sonrası) | backoff ile yeniden bağlan |
| 1013 | Drain sırasında yeni bağlantı reddedildi (`Reconnect` çerçevesi **yok**) | backoff ile yeniden bağlan |

Diğer tüm kodlar (1006 dahil) → backoff ile yeniden bağlan. İstemci **yalnızca 1000** gönderir; ayrıntı `reason`
dizgesindedir (`client-close`, `pong-timeout`, `server-reconnect`, `protocol-error`, `handshake-timeout`, `hello-failed`,
`evicted`).

Sunucunun kapatma çerçevesindeki `reason` dizgesi yalnızca tanılama içindir; istemci kararını koda ve önceki
`Error`/`Reconnect` çerçevesine göre verir. Bugün gönderilen değerler: `bye` (1000), `peer closed` (1000, istemcinin
kapatmasına yanıt), `draining` (1001 ve 1013), `too_many_connections` (1001), `internal error` (1001),
`ProtocolVersionUnsupported` (1002), `InvalidFrame` (1003, 1009), `HelloRequired` ve `Unauthenticated` (1008).

`Reconnect.reason` değerleri sözleşmenin parçasıdır:

- `"draining"` — node kapanıyor. Sunucu herkese aynı üst sınırı gönderir: `delayMs = 10000`;
  istemci `[0, delayMs]` aralığında rastgele bekler ve yeniden bağlanır; sunucu yaklaşık 2 s sonra 1001 ile kapatır.
  İstemci `Reconnect` gecikmesine yalnızca durumu `open` iken uyar; başka durumda gecikmeyi yok sayar ve backoff
  uygular. `delayMs` 0 ise JSON'da yer almaz (proto3 varsayılanı düşürür); eksik = 0.
- `"too_many_connections"` — aynı kullanıcının daha yeni bir bağlantısı canlı, bu bağlantı düşürüldü (`MaxConnectionsPerUser`).
  İstemci **yeniden bağlanmaz** (`closed`, `evicted` olayı); aksi halde iki cihaz sırayla birbirini düşürür.
  Uygulama kararı verir ("başka cihazda oturum açıldı").

`Welcome.sessionId` bu WebSocket bağlantısının kimliğidir (bağlantı başına yeni UUIDv7); kimlik doğrulama oturumunun
(`sessions` satırı) kimliği değildir. Sunucu logları bu değeri gösterir (admin paneli geldiğinde o da gösterecek).

## Yeniden bağlanma ve resume

- Kopuş sonrası üstel geri çekilme: 0,5 s başlangıç, ×2, azami 30 s, ±%20 jitter. `Reconnect{delayMs}` `open` durumunda alındıysa ilk deneme `[0, delayMs]` aralığında rastgele gecikir.
- Yeniden `Hello` gönderilirken `since` haritası açık konuşmalar için `conversationId → uygulanan son seq` taşır (en fazla 50 giriş).
  Sunucu bugün `since`'i ayrıştırır ama kullanmaz; resume M2'de gelir.
- Sunucu boşluğu dolduramazsa (bugün: giden kuyruk taştığında, `full: true`) `ResyncRequired` gönderir; istemci `GET /api/v1/sync` çağırır ve listelenen konuşmaları REST ile doldurur.
- Aynı `id` iki kez gelirse istemci ikincisini atar; aynı `(conversationId, seq)` iki kez gelirse ikincisini atar.

## İstek/yanıt korelasyonu

- Yanıt bekleyen her istemci çerçevesi benzersiz `rid` (UUIDv7) taşır. Sunucu **tam olarak bir** `Ack` veya `Error` döndürür.
- İstemci 15 s içinde yanıt almazsa isteği zaman aşımına düşürür; gönderim istekleri idempotent olduğu için (`clientMsgId`) güvenle yineler.
- `rid` boş olan çerçeveler (Ping, Bye, typing) yanıt beklemez.

## Altın fixture biçimi

`api/fixtures/ws/<isim>.json`:

```json
{
  "description": "Successful handshake followed by a ping round-trip",
  "frames": [
    { "dir": "c2s", "json": { "rid": "0190f0a1-0000-7000-8000-000000000001", "hello": { "protoVersion": 1, "token": "dev:u1", "deviceId": "…" } } },
    { "dir": "s2c", "json": { "id": "0190f0a1-0000-7000-8000-000000000002", "welcome": { "sessionId": "…", "userId": "…", "heartbeatIntervalMs": 25000 } } },
    { "dir": "c2s", "json": { "ping": { "ts": "1725540000000" } } },
    { "dir": "s2c", "json": { "id": "…", "pong": { "ts": "1725540000000" } } }
  ]
}
```

- `dir`: `c2s` istemciden sunucuya, `s2c` sunucudan istemciye, `server-action` sunucu tarafı test kancasına talimat
  (`{"dir":"server-action","action":"drain"}`; hiçbir istemci çerçevesi drain tetikleyemez). İstemci oynatıcısı bu girdileri atlar.
- Üst seviye `"close": {"code": N}` — sunucu son çerçeveden sonra bağlantıyı bu kodla kapatır. Sunucu testleri kodu
  doğrular; istemci testleri kapanışı oynatır ve istemcinin kod tablosundaki beklenen duruma (`closed`/`reconnecting`) geçtiğini doğrular.
- Üst seviye `"clientReplay": false` — dizi istemci tarafı protokol ihlali içerir (Hello öncesi Ping, ikinci Hello,
  desteklenmeyen sürüm); uyumlu bir istemci bunu üretemez, yalnızca sunucu sözleşme testleri çalıştırır. İstemci oynatıcısı atlandığını raporlar.
- Sunucu testleri `c2s` çerçevelerini gönderip `s2c` çerçevelerini **yapısal olarak** doğrular (UUID/zaman alanları `"…"` ise yalnızca varlık ve tip kontrol edilir; `"…"` sayı alanları için de geçerlidir).
- İstemci testleri tersini yapar: sahte sunucu `s2c` çerçevelerini oynatır, istemcinin ürettiği `c2s` çerçeveleri **alt küme** olarak doğrulanır
  (fixture'ın yazdığı her alan eşleşmeli, istemcinin eklediği fazladan alanlar yok sayılır). `c2s` çerçevelerdeki `rid` karşılaştırılmaz
  (istemci üretir); oynatıcı fixture-rid → gerçek-rid eşlemesini sonraki `s2c` yanıtlarına (`Ack`, `Error`) uygular.
  İlk `Hello`'daki `token`, `deviceId` ve `client` alanları test istemcisini yapılandırır. İstemcinin kendiliğinden üretmediği
  `c2s` çerçeveleri (`bye`) oynatıcının tetikleyici kancasıyla üretilir.
- İstemcinin seçtiği alanlar `c2s` karşılaştırmasında yok sayılır: `rid` ve `ping.ts`. `s2c` `pong.ts`, istemcinin gerçekten
  gönderdiği `ping.ts` ile yankılanır (sunucu davranışının aynısı).
- `s2c` çerçevelerdeki `"…"` yer tutucuları istemci oynatıcısı tarafından alan adına göre gerçek değerlerle doldurulur
  (`serverTime`/`*Time`/`*At` → RFC 3339 zaman, `id`/`*Id` → UUIDv7, diğerleri → işaret dizgesi) ve çerçeve `ServerFrame`
  şemasına göre doğrulanır; ayrıştırılamayan bir fixture sessiz bir yeniden bağlanma yerine açık hata verir.
- `int64` alanlar proto3 JSON gereği **string** olarak yazılır (`"ts": "1725540000000"`).
