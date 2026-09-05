# Talky yapılandırması

`talky.exe` tek binary'dir: Windows servisi olarak çalışır ve aynı zamanda bir CLI'dır.
Bu belge **host'un sahip olduğu** ayarları (`Talky:Node`, `Talky:Server`, `Talky:Logging`),
komut satırını ve yapılandırma kaynaklarının önceliğini tanımlar. Modüllerin kendi bölümleri
(`Talky:Database`, `Talky:Realtime`, …) ilgili modül belgelerinde anlatılır.

Bozuk yapılandırma **başlangıçta** hata verir: seçenekler `DataAnnotations` + `IValidateOptions`
ile doğrulanır ve `ValidateOnStart()` ile bağlanır. `talky config validate` aynı doğrulamayı
sunucu başlatmadan çalıştırır.

---

## 1. Yapılandırma kaynakları

Sonraki kaynak öncekini ezer:

| # | Kaynak | Not |
|---|---|---|
| 1 | `appsettings.json` (talky.exe'nin yanında) | Ürünle gelen varsayılanlar. Kuruluma dokunmayın. |
| 2 | `appsettings.{Environment}.json` (talky.exe'nin yanında) | `ASPNETCORE_ENVIRONMENT` (varsayılan `Production`). |
| 3 | `%ProgramData%\Talky\appsettings.json` | **Makine yapılandırması.** MSI burayı yazar, yükseltmede korunur. |
| 4 | `%ProgramData%\Talky\appsettings.Local.json` | Tek makineye özel geçici düzeltmeler. |
| 5 | `--config <dosya>` | Ek JSON dosyası. Dosya yoksa süreç hata ile çıkar. |
| 3–4 (alternatif) | `--data-dir <dizin>` verilirse `<dizin>\appsettings.json` ve `<dizin>\appsettings.Local.json`, `%ProgramData%\Talky` çiftinin yerini alır. |
| 6 | Ortam değişkenleri | Önce ön eksiz, sonra `TALKY_` ön ekli olanlar. |
| 7 | Komut satırı (`--role`, `--urls`, `--set`) | En yüksek öncelik. |

İçerik kökü (content root) her zaman **talky.exe'nin bulunduğu dizindir**; Windows servisi
`C:\Windows\System32` çalışma diziniyle başladığı için bu sabitlenmiştir.

### Ortam değişkeni biçimi

`TALKY_` ön eki `Talky` kök bölümünün yerine geçer ve `__` bölüm ayırıcısıdır:

```
TALKY_Server__DrainSeconds=60          ->  Talky:Server:DrainSeconds = 60
TALKY_Node__Roles=api,worker           ->  Talky:Node:Roles = api,worker
TALKY_Server__Certificate__Thumbprint  ->  Talky:Server:Certificate:Thumbprint
TALKY_Server__Urls__0=https://+:8443   ->  Talky:Server:Urls:0
```

Ön eksiz ortam değişkenleri de okunur (çerçevenin kendi anahtarları için, örn.
`Logging__LogLevel__Default=Debug`), ancak Talky ayarları için `TALKY_` biçimini tercih edin.

> **Dizi uyarısı.** Yapılandırma dizileri **eleman eleman** birleşir. `appsettings.json` üç URL
> tanımlıyorsa ve `%ProgramData%` dosyası bir tane yazıyorsa sonuç üç elemanlıdır (yalnızca ilki
> değişir). Listeyi tümüyle değiştirmek için `--urls` kullanın: bu seçenek `Talky:Server:Urls`
> değerini bütün olarak değiştirir.

---

## 2. Komut satırı

```
talky                       # = talky serve (Windows servisi bunu çalıştırır)
talky serve   [seçenekler]
talky migrate [seçenekler]
talky doctor  [seçenekler]
talky version
talky config validate [seçenekler]
```

Tüm komutlarda geçerli seçenekler:

| Seçenek | Anlamı |
|---|---|
| `--config <dosya>`, `-c` | Ek JSON yapılandırma dosyası (kaynak sırası: 5). |
| `--data-dir <dizin>` | Node veri dizini (varsayılan `%ProgramData%\Talky`). Dizindeki `appsettings.json` ve `appsettings.Local.json` kaynak 3–4 yerine yüklenir; `Talky:Logging:Directory` varsayılanı `<dizin>\logs` olur; çözülen yol `Talky:DataDirectory` anahtarıyla yayımlanır. Windows servisi (MSI / Install-Talky.ps1) bu seçenekle başlar. |
| `--role <roller>` | `Talky:Node:Roles` değerini ezer, örn. `--role api,worker`. |
| `--urls <urls>` | `Talky:Server:Urls` değerini **tümüyle** değiştirir. Virgülle veya boşlukla ayrılır. |
| `--set <anahtar>=<değer>` | Tek bir yapılandırma anahtarını ezer, örn. `--set Talky:Server:DrainSeconds=60`. Tekrarlanabilir. |
| `--help`, `-h` | Yardım. |

Genel host ayarları (`--environment`, `--contentRoot`, `--applicationName`, `--webroot`)
doğrudan host builder'a geçer; dağıtım betikleri ve ASP.NET Core test host'u bunları kullanır.

### Çıkış kodları

| Kod | Anlamı |
|---|---|
| 0 | Başarılı. |
| 1 | Komut çalıştı ama başarısız bildirdi (`doctor` bir Error buldu, `migrate` başarısız oldu, bilinmeyen komut). |
| 2 | Gerekli bir servis kayıtlı değil (`talky migrate` için `IMigrationRunner` yok). |
| 3 | Yapılandırma geçersiz; host başlamayı reddetti. |

`talky doctor` şu yerleşik kontrolleri çalıştırır (modüller kendi `IDoctorCheck`'lerini ekler):
`config`, `certificate`, `identity`, `log-directory`, `os`, `runtime`. Her kontrol paralel çalışır
ve 10 saniye ile sınırlıdır.

---

## 3. `Talky:Node`

| Anahtar | Tür | Varsayılan | Ortam değişkeni | Açıklama |
|---|---|---|---|---|
| `Talky:Node:Id` | string? | *(üretilir)* | `TALKY_Node__Id` | Düğüm kimliği. Boşsa `{MakineAdı}-{4 rastgele base32}` üretilir, yani aynı makinedeki iki süreç çakışmaz. 1–64 karakter: harf, rakam, `.`, `_`, `-`. |
| `Talky:Node:Roles` | string | `all` | `TALKY_Node__Roles` | Virgülle ayrılmış rol listesi. Geçerli değerler: `api`, `worker`, `search`, `push-relay`, `all`. Büyük/küçük harf duyarsız; `pushrelay` ve `push_relay` de kabul edilir. |

Kimliği yalnızca dış bir sistem sabit ad bekliyorsa sabitleyin; sabitlenmiş kimlik aynı makinede
ikinci bir süreç çalıştırmayı imkânsız kılar.

---

## 4. `Talky:Server`

| Anahtar | Tür | Varsayılan | Ortam değişkeni | Açıklama |
|---|---|---|---|---|
| `Talky:Server:Urls` | string[] | `["https://+:8443"]` (Development'ta `["https://localhost:8443"]`) | `TALKY_Server__Urls__0` | Kestrel'in dinlediği adresler. `http://` yalnızca `AllowInsecureHttp` true iken kabul edilir. |
| `Talky:Server:PublicUrl` | string? | *(ilk url)* | `TALKY_Server__PublicUrl` | Yük dengeleyicinin dışarıya verdiği adres; bildirimlerdeki bağlantılarda kullanılır. Mutlak http(s) URL olmalıdır. |
| `Talky:Server:AllowInsecureHttp` | bool | `false` | `TALKY_Server__AllowInsecureHttp` | `http://` uçlarına ve Development dışında geliştirme sertifikasına izin verir. Üretimde **açmayın**. |
| `Talky:Server:DrainSeconds` | int | `30` | `TALKY_Server__DrainSeconds` | Kapanış başladıktan sonra düğümün hizmet vermeye devam ettiği süre. 0–3600. Host'un kapanma zaman aşımı `DrainSeconds + 15` saniyedir. |
| `Talky:Server:KnownProxies` | string[] | `[]` | `TALKY_Server__KnownProxies__0` | `X-Forwarded-*` başlıklarına güvenilen proxy IP adresleri. Liste boşken iletilen başlıklar tümüyle yok sayılır. |

### `Talky:Server:Certificate`

| Anahtar | Tür | Varsayılan | Açıklama |
|---|---|---|---|
| `Source` | `DevCert` \| `Store` \| `Pfx` | `DevCert` | Sertifikanın nereden geleceği. |
| `StoreName` | string | `My` | Windows sertifika deposu adı (`My`, `WebHosting`, …). `Source=Store` için. |
| `StoreLocation` | `LocalMachine` \| `CurrentUser` | `LocalMachine` | Depo konumu. gMSA'nın özel anahtarı okuma izni olmalıdır. |
| `Thumbprint` | string? | – | SHA-1 parmak izi. `Subject`'e tercih edilir. Windows arayüzünden kopyalanan boşluklu biçim kabul edilir. |
| `Subject` | string? | – | `Thumbprint` yokken kullanılan konu (subject) parçası, örn. `talky.corp.local`. |
| `PfxPath` | string? | – | `.pfx` dosya yolu. `Source=Pfx` için zorunlu. |
| `PfxPasswordEnvVar` | string? | – | `.pfx` parolasını taşıyan **ortam değişkeninin adı**. Parolanın kendisi hiçbir zaman yapılandırmada durmaz ve loglanmaz. |

Kurallar:

- `Source=Store` iken `Thumbprint` veya `Subject` zorunludur; ikisi de yoksa başlangıç hata verir.
- Aday sertifikalar arasından önce **geçerlilik süresi içinde olan**, sonra en geç biten seçilir.
- Özel anahtar bu hesaba açık değilse başlangıç hata verir (gMSA'ya "Manage Private Keys" izni verin).
- `.pfx` Windows'ta `MachineKeySet` ile yüklenir; gMSA'nın yüklü kullanıcı profili olmadığı için gereklidir.
- `Source=DevCert` Development dışında **reddedilir** (istisna: `AllowInsecureHttp=true` olan laboratuvar düğümü).
- Sertifika, HTTPS ucu bağlanırken çözümlenir; bu sayede `talky doctor` sertifikası bozuk bir düğümde de çalışır.

Kestrel HTTP/1.1 + HTTP/2 konuşur, `Server` başlığını göndermez, Development dışında HSTS uygular.

---

## 5. `Talky:Logging`

Bu bölüm host'un Serilog sink'lerini yönetir. Çerçevenin kendi `Logging:LogLevel` bölümü kategori
seviyelerini ayarlamaya devam eder.

| Anahtar | Tür | Varsayılan | Ortam değişkeni | Açıklama |
|---|---|---|---|---|
| `Talky:Logging:Directory` | string | `%ProgramData%\Talky\logs` | `TALKY_Logging__Directory` | Günlük döngülü JSON dosyalarının dizini. Ortam değişkenleri genişletilir. Dizin oluşturulamazsa süreç yine başlar, diğer sink'lere yazar ve `talky doctor` sorunu bildirir. |
| `Talky:Logging:RetainedFileCount` | int | `30` | `TALKY_Logging__RetainedFileCount` | Diskte tutulan günlük dosya sayısı. 1–3650. |
| `Talky:Logging:MinimumLevel` | string | `Information` | `TALKY_Logging__MinimumLevel` | `Verbose`, `Debug`, `Information`, `Warning`, `Error`, `Fatal`. |
| `Talky:Logging:EventLog` | bool? | *(servis ise true)* | `TALKY_Logging__EventLog` | Windows olay günlüğüne `Talky` kaynağıyla Warning ve üzeri yazar. |
| `Talky:Logging:Console` | bool? | *(Development ise true)* | `TALKY_Logging__Console` | stdout'a okunabilir log yazar. Windows servisinin konsolu yoktur. |

Dosya sink'i `CompactJsonFormatter` kullanır (`talky<yyyyMMdd>.log`, dosya başına en fazla 256 MiB).
Her olay `Application=talky`, `MachineName` ve `NodeId` ile zenginleştirilir.
`/healthz` ve `/readyz` istek loglarına **girmez**. Belirteçler ve kimlik bilgileri hiçbir zaman loglanmaz.

Olay günlüğü kaynağının önceden var olması gerekir (MSI oluşturur). Yoksa sink sessizce devre
dışı kalır; süreç bu yüzden başlamayı reddetmez.

---

## 6. Uç noktalar

| Yol | Amaç |
|---|---|
| `GET /healthz` | Canlılık. Hiçbir kontrol çalıştırmaz, her zaman `200 {"status":"ok"}` döner. Servis izleyicisi bunu kullanır. |
| `GET /readyz` | Hazır olma. `ready` etiketli sağlık kontrolleri + `drain` kontrolü. Düğüm boşalırken `503`. Yük dengeleyici bunu kullanır. |
| `GET /api/v1/ping` | `{ version, nodeId, roles[], serverTime }`. |
| `GET /openapi/v1.json` | Yalnızca Development. |

Bir modülün hazır olma durumuna katılması için sağlık kontrolünü `ready` etiketiyle kaydetmesi
yeterlidir.

---

## 7. Örnek: tek sunucu

Küçük kurulum: tek makine, tüm roller, `%ProgramData%\Talky\appsettings.json`.

```json
{
  "Talky": {
    "Node": { "Roles": "all" },
    "Server": {
      "Urls": [ "https://+:443" ],
      "PublicUrl": "https://talky.corp.local",
      "DrainSeconds": 30,
      "Certificate": {
        "Source": "Store",
        "StoreName": "My",
        "StoreLocation": "LocalMachine",
        "Thumbprint": "A1B2C3D4E5F60718293A4B5C6D7E8F9012345678"
      }
    },
    "Logging": { "MinimumLevel": "Information", "EventLog": true }
  }
}
```

Kurulum sonrası:

```powershell
talky doctor            # sertifika, kimlik, log dizini, SQL, LiveKit
talky migrate           # veritabanı şeması
sc.exe start Talky
```

---

## 8. Örnek: yük dengeleyici arkasında 3 API düğümü

Ortak `%ProgramData%\Talky\appsettings.json` (üç makinede de aynı):

```json
{
  "Talky": {
    "Server": {
      "Urls": [ "https://+:8443" ],
      "PublicUrl": "https://talky.corp.local",
      "DrainSeconds": 45,
      "KnownProxies": [ "10.20.0.11", "10.20.0.12" ],
      "Certificate": {
        "Source": "Store",
        "StoreLocation": "LocalMachine",
        "StoreName": "My",
        "Subject": "talky.corp.local"
      }
    },
    "Logging": { "Directory": "D:\\Talky\\logs", "RetainedFileCount": 60, "EventLog": true }
  }
}
```

Roller makine başına ayrılır — servis kaydında argüman vermek yerine ortam değişkeni kullanmak
yükseltmelerde daha güvenlidir:

```powershell
# api-01, api-02, api-03
[Environment]::SetEnvironmentVariable('TALKY_Node__Roles', 'api', 'Machine')

# worker-01 (worker + search + push-relay)
[Environment]::SetEnvironmentVariable('TALKY_Node__Roles', 'worker,search,push-relay', 'Machine')
```

Yük dengeleyici sağlık kontrolü: `GET /readyz`, 5 saniyede bir, 2 başarısızlıkta havuzdan çıkar.
Kapanma sırası şudur:

1. `sc.exe stop Talky` veya yükseltme betiği kapanışı tetikler.
2. Host, Kestrel durmadan **önce** boşaltma (drain) aşamasını çalıştırır: `/readyz` 503 döner,
   `IDrainCoordinator.DrainToken` iptal edilir (Realtime `Reconnect` çerçevesi gönderir, mesh
   `NodeBye` yollar, worker'lar yeni iş almayı bırakır).
3. `DrainSeconds` boyunca düğüm mevcut istekleri sunmaya devam eder; yük dengeleyici bu sürede
   düğümü havuzdan çıkarır.
4. Süre dolunca Kestrel ve modüller durur. Host'un sert kapanma sınırı `DrainSeconds + 15` saniyedir.

Bu nedenle `DrainSeconds`, yük dengeleyicinin düğümü havuzdan çıkarma süresinden (kontrol aralığı ×
başarısızlık eşiği) belirgin biçimde büyük olmalıdır — yukarıdaki örnekte 5 s × 2 = 10 s < 45 s.

---

## 9. Sorun giderme

| Belirti | Bakılacak yer |
|---|---|
| Servis başlıyor, hemen duruyor | Olay günlüğü (kaynak `Talky`) ve `%ProgramData%\Talky\logs`. Genelde yapılandırma doğrulaması veya sertifika. |
| `Talky:Server:Certificate:Source is 'DevCert' but the environment is 'Production'` | Gerçek sertifikayı yapılandırın veya laboratuvar düğümünde `AllowInsecureHttp=true`. |
| `No certificate with thumbprint … was found` | `talky doctor` çıktısındaki `certificate` satırı; deponun konumu ve hesabın okuma izni. |
| Log dosyası oluşmuyor | `talky doctor` → `log-directory`. gMSA'nın dizine yazma izni olmalı. |
| Yükseltmede bağlantı kopmaları | `DrainSeconds` yük dengeleyici çıkarma süresinden büyük mü; `/readyz` gerçekten yoklanıyor mu. |
