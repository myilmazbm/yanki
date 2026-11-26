# Yankı yapılandırması

`yanki.exe` tek binary'dir: Windows servisi olarak çalışır ve aynı zamanda bir CLI'dır.
Bu belge **host'un sahip olduğu** ayarları (`Yanki:Node`, `Yanki:Server`, `Yanki:Logging`),
komut satırını ve yapılandırma kaynaklarının önceliğini tanımlar. Modüllerin kendi bölümleri
(`Yanki:Database`, `Yanki:Realtime`, …) ilgili modül belgelerinde anlatılır.

Bozuk yapılandırma **başlangıçta** hata verir: seçenekler `DataAnnotations` + `IValidateOptions`
ile doğrulanır ve `ValidateOnStart()` ile bağlanır. `yanki config validate`, `ValidateOnStart()` ile
kayıtlı tüm seçenekleri (host ve modüller) ve başlangıç korumalarını (`DevCert`, `AllowDevTokens`)
sunucu başlatmadan çalıştırır. Geçerli JSON olmayan bir yapılandırma dosyası da dosya adını ve
ayrıştırıcı mesajını veren tek satırlık bir yapılandırma hatasıdır (çıkış kodu 3).

---

## 1. Yapılandırma kaynakları

Sonraki kaynak öncekini ezer:

| # | Kaynak | Not |
|---|---|---|
| 1 | `appsettings.json` (yanki.exe'nin yanında) | Ürünle gelen varsayılanlar. Kuruluma dokunmayın. |
| 2 | `appsettings.{Environment}.json` (yanki.exe'nin yanında) | `ASPNETCORE_ENVIRONMENT` (varsayılan `Production`). |
| 3 | `%ProgramData%\Yanki\appsettings.json` | **Makine yapılandırması.** `Install-Yanki.ps1` (tam kurulum veya `-ConfigOnly`) şablondan üretir; MSI yapılandırma dosyası yazmaz. Yükseltmede korunur. |
| 4 | `%ProgramData%\Yanki\appsettings.Local.json` | Tek makineye özel geçici düzeltmeler. |
| 5 | `--config <dosya>` | Ek JSON dosyası. Dosya yoksa süreç hata ile çıkar. |
| 3–4 (alternatif) | `--data-dir <dizin>` verilirse `<dizin>\appsettings.json` ve `<dizin>\appsettings.Local.json`, `%ProgramData%\Yanki` çiftinin yerini alır. |
| 6 | Ortam değişkenleri | Önce ön eksiz, sonra `YANKI_` ön ekli olanlar. |
| 7 | Komut satırı (`--role`, `--urls`, `--set`) | En yüksek öncelik. |

İçerik kökü (content root) her zaman **yanki.exe'nin bulunduğu dizindir**; Windows servisi
`C:\Windows\System32` çalışma diziniyle başladığı için bu sabitlenmiştir.

### Ortam değişkeni biçimi

`YANKI_` ön eki `Yanki` kök bölümünün yerine geçer ve `__` bölüm ayırıcısıdır:

```
YANKI_Server__DrainSeconds=60          ->  Yanki:Server:DrainSeconds = 60
YANKI_Node__Roles=api,worker           ->  Yanki:Node:Roles = api,worker
YANKI_Server__Certificate__Thumbprint  ->  Yanki:Server:Certificate:Thumbprint
YANKI_Server__Urls__0=https://+:8443   ->  Yanki:Server:Urls:0
```

Ön eksiz ortam değişkenleri de okunur (`Yanki__Server__DrainSeconds` gibi), ancak Yankı ayarları
için `YANKI_` biçimini tercih edin. Çerçevenin `Logging:LogLevel` bölümü (ve
`Logging__LogLevel__Default` gibi değişkenler) **etkisizdir**: tek log sağlayıcısı Serilog'dur,
seviye `Yanki:Logging:MinimumLevel` ile ayarlanır (bkz. §5).

> **Dizi uyarısı.** Yapılandırma dizileri **eleman eleman** birleşir. `appsettings.json` üç URL
> tanımlıyorsa ve `%ProgramData%` dosyası bir tane yazıyorsa sonuç üç elemanlıdır (yalnızca ilki
> değişir). Listeyi tümüyle değiştirmek için `--urls` kullanın: bu seçenek `Yanki:Server:Urls`
> değerini bütün olarak değiştirir.

---

## 2. Komut satırı

```
yanki                       # = yanki serve (Windows servisi bunu çalıştırır)
yanki serve   [seçenekler]
yanki migrate [seçenekler]
yanki doctor  [seçenekler]
yanki version
yanki config validate [seçenekler]
```

Tüm komutlarda geçerli seçenekler:

| Seçenek | Anlamı |
|---|---|
| `--config <dosya>`, `-c` | Ek JSON yapılandırma dosyası (kaynak sırası: 5). |
| `--data-dir <dizin>` | Node veri dizini (varsayılan `%ProgramData%\Yanki`). Dizindeki `appsettings.json` ve `appsettings.Local.json` kaynak 3–4 yerine yüklenir; `Yanki:Logging:Directory` varsayılanı `<dizin>\logs` olur; çözülen yol `Yanki:DataDirectory` anahtarıyla yayımlanır (**ayrılmış**, henüz hiçbir modül okumaz). Sondaki tırnak ve dizin ayırıcıları atılır. Dizin yoksa hata değildir; dosyaları yüklenmez. Windows servisi (MSI / Install-Yanki.ps1) bu seçenekle başlar. |
| `--role <roller>` | `Yanki:Node:Roles` değerini ezer, örn. `--role api,worker`. |
| `--urls <urls>` | `Yanki:Server:Urls` değerini **tümüyle** değiştirir. Virgülle veya boşlukla ayrılır. |
| `--set <anahtar>=<değer>` | Tek bir yapılandırma anahtarını ezer, örn. `--set Yanki:Server:DrainSeconds=60`. Tekrarlanabilir. |
| `--help`, `-h` | Yardım. |

Genel host ayarları (`--environment`, `--contentRoot`, `--applicationName`, `--webroot`)
doğrudan host builder'a geçer; dağıtım betikleri ve ASP.NET Core test host'u bunları kullanır.

### Çıkış kodları

| Kod | Anlamı |
|---|---|
| 0 | Başarılı. |
| 1 | Komut çalıştı ama başarısız bildirdi (`doctor` bir Error buldu, `migrate` başarısız oldu, bilinmeyen komut veya seçenek, beklenmeyen bir hata). |
| 2 | **Ayrılmış** (eskiden "gerekli servis kayıtlı değil"; artık üretilmez, başka anlamda kullanılmaz). |
| 3 | Yapılandırma geçersiz; host başlamayı reddetti (doğrulama hatası, geçersiz JSON, eksik `--config` dosyası, sertifika veya özel anahtar sorunu, başlangıç koruması). |

Süreci bitiren her hata stderr'e yazılır. Süreç Windows servisi olarak çalışıyorsa (stderr görünmez)
aynı metin ayrıca en iyi çabayla Windows olay günlüğüne (kaynak `Yanki`, Error) ve
`<veri-dizini>\logs\yanki-startup.log` dosyasına zaman damgalı bir satır olarak eklenir; servis için
ilk bakılacak yer bu ikisidir. Elle çalıştırılan komutlar (`doctor`, `config validate`, `migrate` …)
bu ikisine yazmaz. Servis komut satırındaki hatalar (bilinmeyen seçenek, değeri eksik seçenek) da
aynı yoldan raporlanır.

`yanki doctor` şu yerleşik kontrolleri çalıştırır (modüller kendi `IDoctorCheck`'lerini ekler;
bugün Yanki.Data `sql` kontrolünü ekler): `config`, `certificate`, `identity`, `log-directory`,
`os`, `runtime`. Her kontrol paralel çalışır ve 10 saniye ile sınırlıdır. `doctor` onu çalıştıran
hesapla çalışır: `identity` satırı yalnızca hangi hesapla denetlendiğini bildirir, servis hesabının
izinleri (log dizini, özel anahtar) ancak servisin kendisi başlarken sınanır; servis, log dizinine
yazamıyorsa başlangıçta bir uyarı loglar. `config` satırı `serve`'ün uyguladığı geliştirme token'ı
korumasını da uygular. Geçersiz host bölümleri ve geçersiz `Yanki:Database` seçenekleri `doctor`'ı
hiçbir satır yazılmadan çıkış kodu 3 ile bitirir.

---

## 3. `Yanki:Node`

| Anahtar | Tür | Varsayılan | Ortam değişkeni | Açıklama |
|---|---|---|---|---|
| `Yanki:Node:Id` | string? | *(üretilir)* | `YANKI_Node__Id` | Düğüm kimliği. Boşsa `{MakineAdı}-{4 rastgele base32}` üretilir, yani aynı makinedeki iki süreç çakışmaz. 1–64 karakter: harf, rakam, `.`, `_`, `-`. |
| `Yanki:Node:Roles` | string | `all` | `YANKI_Node__Roles` | Virgülle ayrılmış rol listesi. Geçerli değerler: `api`, `worker`, `search`, `push-relay`, `all`. Büyük/küçük harf duyarsız; `pushrelay` ve `push_relay` de kabul edilir. |

Roller M2'ye kadar yalnızca kaydedilir ve raporlanır (`/api/v1/ping`, `yanki doctor`), hiçbir
davranışı açıp kapatmaz. Servis komut satırı yalnızca `--data-dir` içerir (MSI ve `Install-Yanki.ps1`
`--role` geçmez); roller yapılandırma dosyasından (`Install-Yanki.ps1 -Roles` yazar) veya onu ezen
`YANKI_Node__Roles` ortam değişkeninden gelir.

Kimliği yalnızca dış bir sistem sabit ad bekliyorsa sabitleyin; sabitlenmiş kimlik aynı makinede
ikinci bir süreç çalıştırmayı imkânsız kılar.

---

## 4. `Yanki:Server`

| Anahtar | Tür | Varsayılan | Ortam değişkeni | Açıklama |
|---|---|---|---|---|
| `Yanki:Server:Urls` | string[] | `["https://+:8443"]` (Development'ta `["https://localhost:8443"]`) | `YANKI_Server__Urls__0` | Kestrel'in dinlediği adresler. `http://` yalnızca `AllowInsecureHttp` true iken kabul edilir. |
| `Yanki:Server:PublicUrl` | string? | – | `YANKI_Server__PublicUrl` | **Ayrılmış, henüz kullanılmıyor.** Yük dengeleyicinin dışarıya verdiği adres olacak; şimdiden doğrulanır (mutlak http(s) URL). |
| `Yanki:Server:AllowInsecureHttp` | bool | `false` | `YANKI_Server__AllowInsecureHttp` | `http://` uçlarına ve Development dışında geliştirme sertifikasına izin verir. Üretimde **açmayın**. |
| `Yanki:Server:DrainSeconds` | int | `30` | `YANKI_Server__DrainSeconds` | Kapanış başladıktan sonra düğümün hizmet vermeye devam ettiği süre. 0–3600. Host'un kapanma zaman aşımı `DrainSeconds + 15` saniyedir. |
| `Yanki:Server:KnownProxies` | string[] | `[]` | `YANKI_Server__KnownProxies__0` | `X-Forwarded-*` başlıklarına güvenilen proxy IP adresleri. Liste boşken iletilen başlıklar tümüyle yok sayılır. |

### `Yanki:Server:Certificate`

| Anahtar | Tür | Varsayılan | Açıklama |
|---|---|---|---|
| `Source` | `DevCert` \| `Store` \| `Pfx` | `DevCert` | Sertifikanın nereden geleceği. |
| `StoreName` | string | `My` | Windows sertifika deposu adı (`My`, `WebHosting`, …). `Source=Store` için. |
| `StoreLocation` | `LocalMachine` \| `CurrentUser` | `LocalMachine` | Depo konumu. gMSA'nın özel anahtarı okuma izni olmalıdır. |
| `Thumbprint` | string? | – | SHA-1 parmak izi. `Subject`'e tercih edilir. Windows arayüzünden kopyalanan boşluklu biçim kabul edilir. |
| `Subject` | string? | – | `Thumbprint` yokken kullanılan konu (subject) parçası, örn. `yanki.corp.local`. |
| `PfxPath` | string? | – | `.pfx` dosya yolu. `Source=Pfx` için zorunlu; ortam değişkenleri genişletildikten sonra tam (mutlak) yol olmalıdır. |
| `PfxPasswordEnvVar` | string? | – | `.pfx` parolasını taşıyan **ortam değişkeninin adı**. Parolanın kendisi hiçbir zaman yapılandırmada durmaz ve loglanmaz. |

Kurallar:

- `Source=Store` iken `Thumbprint` veya `Subject` zorunludur; ikisi de yoksa başlangıç hata verir.
- Aday sertifikalar arasından önce **geçerlilik süresi içinde olan**, sonra en geç biten seçilir.
  Süresi dolmuş veya henüz geçerli olmayan sertifika reddedilmez: `serve` bir Warning loglar,
  `yanki doctor` Error bildirir.
- `yanki serve` başlarken özel anahtarla bir deneme imzası atar; anahtar servis hesabına açık
  değilse başlangıç hata verir (gMSA'ya "Manage Private Keys" izni verin). Bu `Store` ve `Pfx` için
  aynıdır. Seçilen sertifika (açıklama, parmak izi, bitiş tarihi) Information seviyesinde loglanır.
- `.pfx` Windows'ta `MachineKeySet` ile yüklenir; gMSA'nın yüklü kullanıcı profili olmadığı için gereklidir.
- `Source=DevCert` Development dışında **reddedilir** (istisna: `AllowInsecureHttp=true` olan laboratuvar düğümü).
- Aynı kural `Yanki:Realtime:AllowDevTokens=true` için de geçerlidir: geliştirme belirteçleri herkesin
  her kullanıcı olarak oturum açmasına izin verdiğinden Development dışında başlangıç reddedilir
  (laboratuvar düğümü: `--environment Development` veya `AllowInsecureHttp=true`).
- Sertifika, HTTPS ucu bağlanırken çözümlenir; bu sayede `yanki doctor` sertifikası bozuk bir düğümde de çalışır.

Kestrel HTTP/1.1 + HTTP/2 konuşur, `Server` başlığını göndermez, Development dışında HSTS uygular.

---

## 5. `Yanki:Logging`

Bu bölüm host'un Serilog sink'lerini ve en düşük seviyesini yönetir. Serilog tek log sağlayıcısıdır;
çerçevenin `Logging:LogLevel` bölümü **etkisizdir** ve yapılandırmaya yazılmamalıdır. Kategori
istisnaları kodda sabittir: `Microsoft.AspNetCore` ve `Microsoft.EntityFrameworkCore` Warning,
`Microsoft.Hosting.Lifetime` Information.

| Anahtar | Tür | Varsayılan | Ortam değişkeni | Açıklama |
|---|---|---|---|---|
| `Yanki:Logging:Directory` | string | `%ProgramData%\Yanki\logs` | `YANKI_Logging__Directory` | Günlük döngülü JSON dosyalarının dizini. Ortam değişkenleri genişletilir; sonuç tam (mutlak) yol olmalıdır, göreli yol veya tanımsız `%DEĞİŞKEN%` reddedilir. Dizin oluşturulamazsa süreç yine başlar, diğer sink'lere yazar ve bir Warning loglar; nedeni `yanki doctor` bildirir. |
| `Yanki:Logging:RetainedFileCount` | int | `30` | `YANKI_Logging__RetainedFileCount` | Diskte tutulan günlük dosya sayısı. 1–3650. |
| `Yanki:Logging:MinimumLevel` | string | `Information` | `YANKI_Logging__MinimumLevel` | `Verbose`, `Debug`, `Information`, `Warning`, `Error`, `Fatal`. |
| `Yanki:Logging:EventLog` | bool? | *(servis ise true)* | `YANKI_Logging__EventLog` | Windows olay günlüğüne `Yanki` kaynağıyla Warning ve üzeri yazar. |
| `Yanki:Logging:Console` | bool? | *(Development ise true)* | `YANKI_Logging__Console` | stdout'a okunabilir log yazar. Windows servisinin konsolu yoktur. |

Dosya sink'i `CompactJsonFormatter` kullanır (`yanki<yyyyMMdd>.log`, dosya başına en fazla 256 MiB).
Her olay `Application=yanki`, `MachineName` ve `NodeId` ile zenginleştirilir.
`/healthz` ve `/readyz` istek loglarına **girmez**. Belirteçler ve kimlik bilgileri hiçbir zaman loglanmaz.

Olay günlüğü kaynağının (`Yanki`) önceden var olması gerekir; sink kaynağı kendisi oluşturmaz.
Her iki kurulum yolu (MSI ve `Install-Yanki.ps1`) kaynağı oluşturur. Kaynak yoksa sink yazamaz;
süreç bu yüzden başlamayı reddetmez.

---

## 6. Uç noktalar

| Yol | Amaç |
|---|---|
| `GET /healthz` | Canlılık. Hiçbir kontrol çalıştırmaz, her zaman `200 {"status":"ok"}` döner. Servis izleyicisi bunu kullanır. |
| `GET /readyz` | Hazır olma. `ready` etiketli sağlık kontrolleri + `drain` kontrolü. Düğüm boşalırken `503`. Yük dengeleyici bunu kullanır. Kimlik doğrulamasız olduğundan hata mesajları (`error`) yalnızca Development'ta döner; hatalar sunucu logundadır. |
| `GET /api/v1/ping` | `{ version, nodeId, roles[], serverTime }`. |
| `GET /openapi/v1.json` | Yalnızca Development. |

Bir modülün hazır olma durumuna katılması için sağlık kontrolünü `ready` etiketiyle kaydetmesi
yeterlidir.

---

## 7. Örnek: tek sunucu

Küçük kurulum: tek makine, tüm roller, `%ProgramData%\Yanki\appsettings.json`.

```json
{
  "Yanki": {
    "Node": { "Roles": "all" },
    "Database": {
      "ConnectionString": "Server=sql01.corp.local;Database=Yanki;Integrated Security=true;Encrypt=true"
    },
    "Server": {
      "Urls": [ "https://+:8443" ],
      "PublicUrl": "https://yanki.corp.local",
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
yanki migrate           # veritabanı şeması (yoksa veritabanını da oluşturur)
yanki doctor            # yapılandırma, sertifika, kimlik, log dizini, işletim sistemi, runtime, SQL
sc.exe start Yanki
```

---

## 8. Örnek: yük dengeleyici arkasında 3 API düğümü

Ortak `%ProgramData%\Yanki\appsettings.json` (üç makinede de aynı):

```json
{
  "Yanki": {
    "Database": {
      "ConnectionString": "Server=yanki-ag.corp.local;Database=Yanki;Integrated Security=true;Encrypt=true;MultiSubnetFailover=true"
    },
    "Server": {
      "Urls": [ "https://+:8443" ],
      "PublicUrl": "https://yanki.corp.local",
      "DrainSeconds": 45,
      "KnownProxies": [ "10.20.0.11", "10.20.0.12" ],
      "Certificate": {
        "Source": "Store",
        "StoreLocation": "LocalMachine",
        "StoreName": "My",
        "Subject": "yanki.corp.local"
      }
    },
    "Logging": { "Directory": "D:\\Yanki\\logs", "RetainedFileCount": 60, "EventLog": true }
  }
}
```

Roller makine başına ayrılır (M2'ye kadar yalnızca bilgi amaçlıdır, bkz. §3). Ortak dosyayı
değiştirmeden makine ortam değişkeniyle ezilebilir; servis komut satırında `--role` olmadığı için
bu değer geçerli olur:

```powershell
# api-01, api-02, api-03
[Environment]::SetEnvironmentVariable('YANKI_Node__Roles', 'api', 'Machine')

# worker-01 (worker + search + push-relay)
[Environment]::SetEnvironmentVariable('YANKI_Node__Roles', 'worker,search,push-relay', 'Machine')
```

Yük dengeleyici sağlık kontrolü: `GET /readyz`, 5 saniyede bir, 2 başarısızlıkta havuzdan çıkar.
Kapanma sırası şudur:

1. `sc.exe stop Yanki` veya yükseltme betiği kapanışı tetikler.
2. Host, Kestrel durmadan **önce** boşaltma (drain) aşamasını çalıştırır: `/readyz` 503 döner,
   `IDrainCoordinator.DrainToken` iptal edilir (Realtime `Reconnect` çerçevesi gönderir; M2'den
   itibaren mesh `NodeBye` yollar ve worker'lar yeni iş almayı bırakır).
3. `DrainSeconds` boyunca düğüm mevcut istekleri sunmaya devam eder; yük dengeleyici bu sürede
   düğümü havuzdan çıkarır.
4. Süre dolunca Kestrel ve modüller durur. Host'un sert kapanma sınırı `DrainSeconds + 15` saniyedir.

Bu nedenle `DrainSeconds`, yük dengeleyicinin düğümü havuzdan çıkarma süresinden (kontrol aralığı ×
başarısızlık eşiği) belirgin biçimde büyük olmalıdır — yukarıdaki örnekte 5 s × 2 = 10 s < 45 s.

---

## 9. Sorun giderme

| Belirti | Bakılacak yer |
|---|---|
| Servis başlıyor, hemen duruyor | Olay günlüğü (kaynak `Yanki`) ve `%ProgramData%\Yanki\logs\yanki-startup.log`. Genelde yapılandırma doğrulaması, geçersiz JSON veya sertifika. |
| `Yanki:Server:Certificate:Source is 'DevCert' but the environment is 'Production'` | Gerçek sertifikayı yapılandırın veya laboratuvar düğümünde `AllowInsecureHttp=true`. |
| `No certificate with thumbprint … was found` | `yanki doctor` çıktısındaki `certificate` satırı; deponun konumu ve hesabın okuma izni. |
| Log dosyası oluşmuyor | `yanki doctor` → `log-directory`. gMSA'nın dizine yazma izni olmalı. |
| Yükseltmede bağlantı kopmaları | `DrainSeconds` yük dengeleyici çıkarma süresinden büyük mü; `/readyz` gerçekten yoklanıyor mu. |
