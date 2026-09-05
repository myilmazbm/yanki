# Talky gerçek zamanlı protokolü (v1)

Tek gerçek kaynak: `api/proto/talky/v1/frames.proto`. Bu belge proto'da ifade edilemeyen
davranış kurallarını tanımlar. Sunucu (`Talky.Realtime`) ve istemci (`@talky/core`) bu kurallara
uyar; `api/fixtures/ws/*.json` altın akışları her iki tarafın testlerinde çalıştırılır.

## Kodlama

- Bir WebSocket **text** çerçevesi = bir proto3-JSON nesnesi (`ClientFrame` veya `ServerFrame`).
- `oneof body` alan adı çerçeve tipidir: `{"rid":"…","hello":{…}}`, `{"id":"…","welcome":{…}}`.
- Alan adları proto3 JSON'un varsayılan lowerCamelCase biçimindedir (`protoVersion`, `heartbeatIntervalMs`).
- Bilinmeyen alanlar yok sayılır (ileri uyumluluk). Bilinmeyen `oneof` gövdesi = boş gövde → `INVALID_FRAME`.
- Azami çerçeve boyutu varsayılan 64 KiB (`Talky:Realtime:MaxFrameBytes`); aşılırsa `Error{INVALID_FRAME}` ve kapatma kodu 1009.

## El sıkışma

1. İstemci bağlanır ve **5 sn** içinde `Hello` gönderir; aksi halde sunucu `Error{HELLO_REQUIRED}` gönderip 1008 ile kapatır.
2. `protoVersion != 1` → `Error{PROTOCOL_VERSION_UNSUPPORTED}`, kapatma 1002.
3. Token doğrulanamazsa → `Error{UNAUTHENTICATED}`, kapatma 1008.
4. Başarı → `Welcome`. `Hello`'dan sonra ikinci bir `Hello` → `Error{ALREADY_AUTHENTICATED}` (bağlantı açık kalır).
5. `Welcome`'dan önce gelen diğer tüm çerçeveler → `Error{HELLO_REQUIRED}`, kapatma 1008.

## Canlılık (heartbeat) modeli

İki ayrı mekanizma vardır; ikisi de gereklidir:

| Yön | Mekanizma | Neden |
|---|---|---|
| Sunucu → istemci ölü mü? | **WebSocket protokol ping/pong** (RFC 6455 kontrol çerçeveleri). Sunucu `KeepAliveInterval` (= `heartbeatIntervalMs`, varsayılan 25 s) ile ping atar, `KeepAliveTimeout` (2×) içinde pong gelmezse kapatır. | Tarayıcılar protokol ping'ine otomatik pong verir; istemci kodu yazmaz. |
| İstemci → sunucu ölü mü? | **Uygulama seviyesi `Ping`/`Pong` çerçeveleri.** İstemci `Welcome.heartbeatIntervalMs` aralığıyla `Ping{ts}` gönderir; `Pong` 10 s içinde gelmezse bağlantıyı ölü sayar ve yeniden bağlanır. | JS, protokol ping'ini göremez; ölü sunucuyu ancak uygulama çerçevesiyle fark eder. |

`Pong.ts` istemcinin gönderdiği değeri aynen döndürür; RTT ölçümü buradan yapılır.
İstemci 25 s'lik pencerede zaten başka çerçeve gönderdiyse `Ping` atlanabilir; sunucu bunu ping sayar.

## Yeniden bağlanma ve resume

- Kopuş sonrası üstel geri çekilme: 0,5 s başlangıç, ×2, azami 30 s, ±%20 jitter. `Reconnect{delayMs}` alındıysa ilk deneme `[0, delayMs]` aralığında rastgele gecikir.
- Yeniden `Hello` gönderilirken `since` haritası açık konuşmalar için `conversationId → uygulanan son seq` taşır (en fazla 50 giriş).
- Sunucu boşluğu dolduramazsa `ResyncRequired` gönderir; istemci `GET /api/v1/sync` çağırır ve listelenen konuşmaları REST ile doldurur.
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

- `dir`: `c2s` istemciden sunucuya, `s2c` sunucudan istemciye.
- Sunucu testleri `c2s` çerçevelerini gönderip `s2c` çerçevelerini **yapısal olarak** doğrular (UUID/zaman alanları `"…"` ise yalnızca varlık ve tip kontrol edilir).
- İstemci testleri tersini yapar: sahte sunucu `s2c` çerçevelerini oynatır, istemcinin ürettiği `c2s` çerçeveleri doğrulanır.
- `int64` alanlar proto3 JSON gereği **string** olarak yazılır (`"ts": "1725540000000"`).
