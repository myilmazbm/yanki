# Talky işletim kılavuzu

Windows Server 2022/2025 üzerinde Talky kurmak, yükseltmek, yedeklemek ve arızasını bulmak.
Mimari için `docs/architecture.md`, medya spike'ı için `docs/livekit-windows-spike.md`.

Bütün betikler `deploy/windows/` ve `deploy/livekit/` altındadır ve PowerShell 5.1 uyumludur.
Durum değiştiren betikler yükseltilmiş oturum ister ve **`-WhatIf` destekler**; bir sunucuda ilk
kez çalıştırmadan önce her zaman `-WhatIf` ile çalıştırın. İstisnalar: salt okunur
`Test-TalkyPrerequisites.ps1` (değiştirecek bir şey yok) ve yalnızca atılabilir VM için olan
`Test-TalkyInstall.ps1` (`-AcknowledgeServiceInstall` ister).

> **Durum (M0).** Bu kılavuz v1'in hedeflenen işletimini anlatır. M0'da yalnızca host
> (`talky.exe`), CLI (`serve`, `migrate`, `doctor`, `config`), SQL şeması, `/ws` el sıkışması
> ve kurulum betikleri/MSI vardır. Mesh, rol ayrımı, admin paneli, dosya payı ve arama
> aşağıda **planlı** olarak, kilometre taşıyla işaretlidir; takvim `docs/plan-v1.md` bölüm 9.

---

## 1. Ön koşullar

```powershell
.\deploy\windows\Test-TalkyPrerequisites.ps1 `
    -ServiceAccount 'CORP\talky$' `
    -SqlConnectionString 'Server=sql-ag.corp;Database=Talky;Integrated Security=true;Encrypt=true' `
    -CertificateThumbprint <parmak-izi> `
    -FileShare \\corp\dfs\talky\files
```

Denetlenenler: işletim sistemi sürümü ve mimarisi, yükseltilmiş oturum, gMSA'nın
`Test-ADServiceAccount` ile çözülmesi, hesabın `SeServiceLogonRight` hakkı, SQL erişimi ve
sürümü, 8443 portunun (ve `-EnableHttp` ile 8080'in) boş olması, sertifikanın varlığı ve özel anahtarı, dosya
payına erişim, saat kaynağı, disk alanı. Herhangi bir `Fail` varsa çıkış kodu 1'dir.

.NET çalışma zamanı **gerekmez**: `talky.exe` self-contained yayımlanır.

---

## 2. gMSA ve SPN

Talky düğümleri bir **group Managed Service Account** altında çalışır. gMSA parolasızdır:
parola AD tarafından üretilir ve otomatik döndürülür, hiçbir yerde saklanmaz. Bu yüzden
`Install-Talky.ps1` bilinçli olarak parola parametresi kabul etmez.

Bir kere, bir domain denetleyicisinde (KDS kök anahtarı yoksa):

```powershell
Add-KdsRootKey -EffectiveTime ((Get-Date).AddHours(-10))
```

Hesabı oluşturun ve hangi sunucuların parolasını alabileceğini belirtin:

```powershell
New-ADServiceAccount -Name talky `
    -DNSHostName talky.corp `
    -PrincipalsAllowedToRetrieveManagedPassword 'TalkyNodes' `
    -ServicePrincipalNames 'HTTP/talky.corp','HTTP/talky.corp.local'
```

`TalkyNodes`, düğüm bilgisayar hesaplarını içeren bir güvenlik grubudur. Grup üyeliği
makinenin Kerberos biletine ancak yeniden başlatmadan sonra yansır.

Her düğümde:

```powershell
Install-ADServiceAccount -Identity talky
Test-ADServiceAccount -Identity talky     # True dönmeli
```

**"Log on as a service"** hakkı grup ilkesiyle verilir (tercih edilen yol):
`Computer Configuration → Windows Settings → Security Settings → Local Policies →
User Rights Assignment → Log on as a service` altına `CORP\talky$` eklenir.
`Test-TalkyPrerequisites.ps1` bu hakkı denetler ve eksikse uyarır.

**SPN**: istemcilerin Kerberos ile bağlanabilmesi için `HTTP/talky.corp` SPN'i gMSA'ya ait
olmalıdır. Yük dengeleyici arkasında tek bir isim kullanıldığı için SPN de tektir.
Doğrulama:

```powershell
setspn -L CORP\talky$
setspn -Q HTTP/talky.corp        # tek bir hesapta çıkmalı; çift kayıt Kerberos'u bozar
```

---

## 3. Sertifikalar

Kestrel TLS'i sonlandırır ve sertifikayı **Windows sertifika deposundan** okur; PFX dosyası
diske konmaz.

1. Sertifikayı `LocalMachine\My` deposuna, özel anahtarıyla birlikte içe aktarın
   (konu adı `talky.corp`, SAN'larda kullanılan tüm adlar).
2. gMSA'ya özel anahtar okuma izni verin — bu adım unutulursa servis başlar ama TLS el
   sıkışması başarısız olur:

```powershell
$cert = Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Thumbprint -eq '<parmak-izi>' }
$keyFile = $cert.PrivateKey.Key.UniqueName          # CNG
$keyPath = Join-Path $env:ProgramData "Microsoft\Crypto\Keys\$keyFile"
$acl = Get-Acl $keyPath
$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule('CORP\talky$','Read','Allow')))
Set-Acl -Path $keyPath -AclObject $acl
```

3. Parmak izini `Install-Talky.ps1 -CertificateThumbprint` ile verin; betik onu
   `Talky:Server:Certificate:Thumbprint` alanına yazar (kaynak `Store`). `-Urls` içinde
   https varken parmak izi verilmezse betik hiçbir şey yazmadan durur. Sertifikasız
   laboratuvar düğümü için yalnızca http URL'leri verin (`-Urls http://+:8080`); betik o
   zaman `AllowInsecureHttp=true` yazar. Güvenlik duvarı adımı `-Urls`'i okumaz: yalnızca
   8443'ü (ve `-EnableHttp` ile 8080'i) açar. http portu açılmadıysa betik bunu uyarı olarak
   söyler ve açacak komutu verir: `Set-TalkyFirewall.ps1 -EnableHttp -HttpPort <port>`.

LiveKit'in gömülü TURN'ü ayrı bir **PEM** çifti ister (`turn.cert_file` / `turn.key_file`)
ve bunlar dosya olarak durur; TLS'i yük dengeleyici sonlandırıyorsa `external_tls: true`
kullanın ve dosyaları hiç oluşturmayın.

---

## 4. Kurulum

### 4.1 Tek sunucu (küçük kurulum ve laboratuvar)

```powershell
.\deploy\windows\Install-Talky.ps1 `
    -ServiceAccount 'CORP\talky$' `
    -Roles all `
    -SqlConnectionString 'Server=sql01.corp;Database=Talky;Integrated Security=true;Encrypt=true' `
    -CertificateThumbprint <parmak-izi> `
    -FileShare \\corp\dfs\talky\files `
    -WhatIf
```

`-WhatIf` çıktısı beklediğinizle örtüşüyorsa aynı komutu `-WhatIf` olmadan çalıştırın.

**Betik yolu** — `Install-Talky.ps1` sırayla: klasörleri açar, ACL'leri uygular
(`InstallDir` üzerinde okuma+çalıştırma, `DataDir`/`logs`/`index` üzerinde değiştirme),
Windows Olay Günlüğü kaynağı `Talky`'yi (Application) oluşturur, yayın çıktısını kopyalar,
`appsettings.json`'ı şablondan üretir, `talky migrate` ile şemayı uygular, servisi
**Automatic (Delayed Start)** olarak `"<InstallDir>\talky.exe" --data-dir "<DataDir>"`
komut satırıyla kaydeder, kurtarma eylemlerini 5 sn / 10 sn / 30 sn olarak ayarlar, güvenlik
duvarı kurallarını ekler (`Set-TalkyFirewall.ps1`), servisi başlatır ve `talky doctor`
çalıştırır. Servis başlamazsa (ya da Running bildirip 5 sn içinde düşerse) `doctor` yine
çalışır, betik hata verir ve `<DataDir>\logs`'u gösterir.

Var olan bir düğümde betik `<DataDir>\appsettings.json`'ı **korur** (bunu tek satırla
söyler); dosya yalnızca `-ConfigOnly` veya `-Force` ile, zaman damgalı bir yedek alınarak
yeniden üretilir. Dosya korunurken `-Urls`, `-SqlConnectionString`, `-CertificateThumbprint`,
`-Roles`, `-NodeId` veya `-FileShare` verilmişse betik bunların **uygulanmadığını** uyarı
olarak söyler. Var olan servisin oturum hesabı yalnızca `-ServiceAccount` verildiğinde
değişir. Servis zaten varsa ve `-DataDir` verilmemişse veri klasörü servisin komut
satırındaki `--data-dir`'den alınır; verilen `-DataDir` ondan farklıysa ya da komut satırı
okunamazsa betik iki yolu da söyleyerek durur. İstisna `-ConfigOnly`'dir: servise dokunmadığı
için farklı bir `-DataDir` (örneğin bir önizleme klasörü) iki yolu da söyleyen bir uyarıyla
kabul edilir ve yapılandırma oraya yazılır. Şemayı bir DBA uyguluyorsa `-SkipMigrate`
verin.

**MSI yolu** (kurumsal dağıtım araçları için). MSI dosyaları kurar, servisi **durmuş**
olarak kaydeder ve Olay Günlüğü kaynağını oluşturur; `appsettings.json` yazmaz, güvenlik
duvarına dokunmaz ve servisi başlatmaz. Sıra:

```powershell
# msiexec bir GUI programıdır: PowerShell onu beklemeden sonraki satıra geçer. -Wait ile
# kurulumun bitmesini bekleyin ve çıkış kodunu denetleyin (0 dışı her kod durdurur;
# 3010 = yeniden başlatma gerekli).
$p = Start-Process msiexec.exe -Wait -PassThru -ArgumentList '/i','Talky.Server-<sürüm>.msi','SERVICE_ACCOUNT=CORP\talky$','DATADIR=D:\TalkyData','/qn','/norestart','/l*v','install.log'
if ($p.ExitCode -ne 0) { throw "msiexec exited with $($p.ExitCode); see install.log" }
.\deploy\windows\Install-Talky.ps1 -AclOnly -ServiceAccount 'CORP\talky$' -DataDir 'D:\TalkyData'
.\deploy\windows\Install-Talky.ps1 -ConfigOnly -DataDir 'D:\TalkyData' -SqlConnectionString '…' -CertificateThumbprint '…'
& 'C:\Program Files\Talky\talky.exe' migrate --data-dir 'D:\TalkyData'
.\deploy\windows\Set-TalkyFirewall.ps1
Start-Service Talky
& 'C:\Program Files\Talky\talky.exe' doctor --data-dir 'D:\TalkyData'
```

Boşluk içeren bir değeri tırnakla verin: `'DATADIR="D:\Talky Data"'`.

MSI, LocalSystem'e varsayılan verir ve gMSA'yı `SERVICE_ACCOUNT` ile alır; `SERVICE_PASSWORD`
her zaman boş kalır. Roller komut satırında değil yalnızca `appsettings.json`'da
(`Talky:Node:Roles`) durur. MSI yükseltmesi de servisi başlatmaz; yükseltmeden sonra
`Start-Service Talky` gerekir.

**MSI `SERVICE_ACCOUNT` ve `DATADIR` değerlerini hatırlar.** Kurulum iki değeri
`HKLM\SOFTWARE\Talky` altına (`ServiceAccount`, `DataDir`) yazar; sonraki yükseltme, onarım
ve yeniden kurulum bu değerleri oradan okur, bu yüzden yükseltme ve geri alma komutlarında
tekrarlanmaları gerekmez. Öncelik: komut satırında verilen değer > hatırlanan değer >
varsayılan (LocalSystem, `C:\ProgramData\Talky`). `SERVICE_PASSWORD` hiçbir yere yazılmaz.
Hesabı ya da veri klasörünü **bilerek** değiştirmek için yeni değeri bir sonraki
`msiexec /i` komutunda verin (`SERVICE_ACCOUNT=…` / `DATADIR=…`); o değer servise uygulanır
ve yeni hatırlanan değer olur (parolalı bir hesapta `SERVICE_PASSWORD=` de gerekir; veri
klasörünü değiştirirseniz `appsettings.json`'u ve ACL'leri yeni klasöre siz taşırsınız).
Anahtar, `DATADIR` gibi kalıcıdır: `msiexec /x` onu silmez, böylece kaldırıp eski sürümü
kurmak (geri alma) ve yeniden kurulum aynı hesabı ve klasörü bulur. Düğümü tamamen
temizlerken anahtarı elle silin: `Remove-Item HKLM:\SOFTWARE\Talky`. Bu davranış bu
özelliği taşıyan sürümlerden itibaren geçerlidir; anahtarı okumayan daha eski bir MSI'ye
geri dönülürse iki değer o komutta verilmelidir.

**Kurtarma ayarları.** MSI, servisi kaydettikten hemen sonra `sc.exe failure Talky
reset= 86400 actions= restart/5000/restart/10000/restart/30000` çalıştırır (betik yolundaki
ayarın aynısı); komut başarısız olursa kurulum geri alınır. Windows Installer'ın kendi
`MsiServiceConfigFailureActions` tablosu Windows Server 2025'te yeniden başlatma eylemlerini
ayarlayamıyor (hata 1939, erişim reddedildi) ve kurulumu geri alıyordu; bu yüzden
kullanılmıyor. Kurulumdan sonra doğrulayın:

```powershell
sc.exe qfailure Talky      # RESET_PERIOD 86400, RESTART 5000/10000/30000 görmelisiniz
sc.exe qc Talky            # START_TYPE AUTO_START (DELAYED)
```

Bilinen açıklar:

- Host, arızalanan bir arka plan servisi yüzünden kendini durdurduğunda süreç bugün 0
  koduyla çıkar; SCM bunu hata saymaz ve kurtarma eylemleri tetiklenmez. Doğru çıkış kodu ve
  `sc.exe failureflag` ayarı gerçek bir servis kurulumunda doğrulanınca gelecek.
- MSI'nin kendi servis durdurması boşaltmayı (`DrainSeconds`, varsayılan 30 sn) beklemek
  için tasarlanmamıştır. **Bir kez ölçüldü** (2026-11-25, laboratuvar VM'i, Windows Server
  2025): çalışan bir düğümde `msiexec /x … /qn /norestart` 31,3 sn sonra 0 koduyla çıktı;
  `StopServices` 30 sn'lik boşaltmanın bitmesini bekledi, servis ve dosyalar kaldırıldı, veri
  klasörü ve `appsettings.json` kaldı, yeniden başlatma gerekmedi. Tek bir ölçüm olduğu ve
  daha uzun bir `DrainSeconds` ile sonucun değişebileceği için prosedür değişmedi: MSI
  yükseltmesi, kaldırması ve geri alması her zaman önce `Stop-Service Talky` ile başlar
  (bölüm 6 ve 10). Çalışan düğüme karşı yükseltme amaçlı `msiexec /i` ölçülmedi.

**Kabul testi.** `deploy\windows\Test-TalkyInstall.ps1` her iki yolu gerçekten kurar ve
`/api/v1/ping`'in düğüm kimliğini döndüğünü denetler (M0 çıkış kriteri); MSI yolunda ayrıca
gecikmeli otomatik başlatmayı ve 5/10/30 sn kurtarma ayarını denetler. `-UpgradeMsiPath` ile
verilen daha yüksek sürümlü ikinci bir MSI (`Build-Msi.ps1 -Version 0.1.1 -SkipPublish`),
özel `SERVICE_ACCOUNT` ve `DATADIR` ile kurulmuş bir düğümün bu iki değeri vermeden
yükseltildiğinde hesabını, `--data-dir`'ini ve kurtarma ayarını koruduğunu denetler. Servis
kurup kaldırdığı için yalnızca atılabilir bir VM'de, yükseltilmiş oturumda ve
`-AcknowledgeServiceInstall` ile çalıştırılır. 2026-11-25'te laboratuvar VM'inde (Windows
Server 2025, SQL Server 2022) yükseltme alt adımıyla birlikte tüm denetimleri geçti
(`docs/plan-v1.md`, "M0 durumu").

**SQL izinleri.** `talky migrate` kurulumu yapan yönetici hesabıyla (Integrated Security
ise o Windows kimliğiyle) çalışır: veritabanı yoksa oluşturur (`dbcreator`), varsa üzerinde
`db_owner` gerekir (`ALTER DATABASE … READ_COMMITTED_SNAPSHOT` ve şema değişiklikleri); kilit
için `master`'da `sp_getapplock` kullanır. Servis hesabı (gMSA) şema değiştirmez: veritabanında
bir login/kullanıcı ile `db_datareader` ve `db_datawriter` yeterlidir.
`Test-TalkyPrerequisites.ps1` SQL'i betiği çalıştıran hesapla dener, gMSA ile değil; gMSA
login'ini DBA ile ayrıca doğrulayın. Şemayı DBA uyguluyorsa ona `talky migrate`'i (veya
`-SkipMigrate` ile kurulum sonrası) çalıştırtın.

### 4.2 Üç düğümlü küme (planlı, M2)

Düğümler arası yayın (gRPC mesh) ve rol ayrımı **M2**'de gelir. M0'da birden fazla düğüm
kurulabilir ama düğümler birbirini tanımaz; yük dengeleyici arkasında birden çok `api`
düğümü çapraz düğüm dağıtımı olmadan çalışır. `Talky:Node:Roles` bugün yalnızca bilgi
amaçlıdır (`/api/v1/ping` gösterir), hiçbir şeyi açıp kapatmaz.

Hedef yerleşim: iki `api` düğümü, bir `search` düğümü (M3), ayrı SQL AG, ayrı LiveKit
düğümü. Düğüm keşfi SQL'deki `cluster_nodes` kaydıyla yapılacak; mesh için gerekenleri
(port, güvenlik duvarı kuralı) M2 ekler.

```powershell
# node-01 ve node-02 (api + worker)
.\deploy\windows\Install-Talky.ps1 `
    -ServiceAccount 'CORP\talky$' -Roles api,worker `
    -SqlConnectionString 'Server=talky-ag.corp;Database=Talky;Integrated Security=true;Encrypt=true' `
    -CertificateThumbprint <parmak-izi>

# node-03 (search, M3)
.\deploy\windows\Install-Talky.ps1 `
    -ServiceAccount 'CORP\talky$' -Roles search `
    -SqlConnectionString 'Server=talky-ag.corp;Database=Talky;Integrated Security=true;Encrypt=true' `
    -CertificateThumbprint <parmak-izi> `
    -DataDir D:\TalkyData          # indeks NVMe üzerinde olsun
```

`talky migrate` yalnızca ilk düğümde gereklidir; diğerlerinde zararsızdır (kilit alır,
bekleyen göç yoksa hiçbir şey yapmaz).

Medya düğümü:

```powershell
.\deploy\livekit\Get-LiveKit.ps1 -Version 1.13.6
# livekit.yaml içindeki "<...>" yer tutucularını doldurun
.\deploy\livekit\Install-LiveKitService.ps1 -SignalRemoteAddress 10.20.30.0/24
```

### 4.3 Portlar

| Port | Protokol | Nerede | Kim erişir |
|---|---|---|---|
| 8443 | TCP | Talky düğümü | İstemciler / yük dengeleyici |
| 8080 | TCP | Talky düğümü | Yalnızca TLS'i yük dengeleyici sonlandırıyorsa (`-EnableHttp`) |
| 9443 | TCP | Talky düğümü | Planlı (M2, mesh): yalnızca diğer Talky düğümleri. Bugün açılmaz |
| 7880 | TCP | LiveKit | Yalnızca Talky `api` düğümleri |
| 7881 | TCP | LiveKit | İstemciler (UDP engelliyse) |
| 7882 | UDP | LiveKit | İstemciler |
| 3478 | UDP | LiveKit | İstemciler (TURN) |
| 5349 | TCP | LiveKit | İstemciler (TURN/TLS) |
| 30000–40000 | UDP | LiveKit | İstemciler (TURN aktarım tahsisleri) |

---

## 5. Yük dengeleyici

- **Sağlık probu:** `GET https://<düğüm>:8443/readyz`. 200 → düğüm trafik alabilir,
  503 → almamalı. Prob aralığı 5 sn, 2 ardışık başarısızlıkta havuzdan çıkarma.
  `/healthz` yalnızca sürecin ayakta olduğunu söyler; **yük dengeleyici `/readyz`
  kullanmalıdır**, aksi halde boşaltma çalışmaz.
- **Oturum yapışkanlığı gerekmez.** WebSocket bağlantısı doğal olarak bir düğüme bağlıdır;
  kopunca istemci yeniden bağlanır ve başka bir düğüme düşebilir — `seq` tabanlı resync bunu
  görünmez kılar.
- **Zaman aşımları:** boşta kalan WebSocket bağlantısı için en az 120 sn (uygulama
  heartbeat'i 25 sn'dir). Daha kısa bir değer bağlantıları gereksiz yere koparır.
- TLS'i yük dengeleyici sonlandırıyorsa arka uçta 8080 kullanılabilir; bu durumda 8080
  yalnızca yük dengeleyicinin alt ağına açılmalıdır (`Set-TalkyFirewall.ps1 -EnableHttp`).
  `-EnableHttp` yalnızca güvenlik duvarını açar; `<DataDir>\appsettings.json`'da şu üç
  ayarı elle yapın (betik http/https karışık listeyi bilerek reddeder):
  `Talky:Server:Urls` içine `http://+:8080`, `Talky:Server:AllowInsecureHttp: true` ve
  `Talky:Server:KnownProxies` içine yük dengeleyicinin IP adresleri. Var olan dosya
  sonraki kurulumlarda korunur.

---

## 6. Yuvarlanan yükseltme

Amaç: kullanıcı bir toplantıdan düşmez, mesaj kaybolmaz, yeniden bağlanma dalgası tek bir
düğüme yığılmaz.

Düğüm başına sıra (aynı anda **tek** düğüm):

1. **Boşalt.** Düğüme boşaltma sinyali verilir. `/readyz` derhal **503** dönmeye başlar;
   yük dengeleyici düğümü havuzdan çıkarır ve yeni bağlantı gelmez.
2. **İstemcileri nazikçe gönder.** Düğüm açık WebSocket oturumlarına
   `Reconnect{delayMs: 10000}` çerçevesi gönderir; boşaltma sırasında
   gelen yeni bağlantılar 1013 koduyla kapatılır. İstemciler yeniden bağlanmayı
   `[0, delayMs]` aralığında rastgele geciktirir — en fazla 10 sn'lik pencere, tek bir
   düğüme yığılan sürü etkisini önler.
3. **Boşaltma süresini bekle** (`Talky:Server:DrainSeconds`, varsayılan 30 sn). Bu süre
   boyunca düğüm devam eden istekleri tamamlar; yeni istek almaz.
4. **Şemayı uygula — yalnızca ilk yükseltilen düğümden, bir kez.** Yeni sürümün
   `talky migrate`'i, diğer düğümler yeniden başlamadan önce çalışır. `Install-Talky.ps1`
   bunu servisi başlatmadan önce kendisi yapar; sonraki düğümlerde tekrar çalışması
   zararsızdır (bekleyen göç yoktur) ya da `-SkipMigrate` ile atlanır.
5. **Durdur, değiştir, başlat.**

```powershell
# 1-3: sürücü betiği yerine servis üzerinden en basit yol
Stop-Service Talky            # boşaltma koordinatörü 503 + Reconnect + boşaltma süresini kendi yürütür
.\deploy\windows\Install-Talky.ps1 -SourceDir <yeni-yayın>
# Özel veri klasörüyle kurulmuş düğümde (ör. node-03) klasörü açıkça verin:
#   .\deploy\windows\Install-Talky.ps1 -SourceDir <yeni-yayın> -DataDir D:\TalkyData
# Install-Talky.ps1 servisi zaten durdurur, dosyaları değiştirir, migrate çalıştırır ve
# başlatır; var olan appsettings.json ve servis hesabı korunur. -DataDir verilmezse var
# olan servisin --data-dir'i kullanılır; verilen -DataDir ondan farklıysa betik durur.
```

MSI ile kurulmuş düğümde aynı adım (`SERVICE_ACCOUNT` ve `DATADIR` hatırlanır, verilmez;
bölüm 4.1):

```powershell
Stop-Service Talky            # önce: MSI'nin kendi durdurması boşaltmayı beklemek için tasarlanmadı
$p = Start-Process msiexec.exe -Wait -PassThru -ArgumentList '/i','Talky.Server-<yeni-sürüm>.msi','/qn','/norestart','/l*v','upgrade.log'
if ($p.ExitCode -ne 0) { throw "msiexec exited with $($p.ExitCode); see upgrade.log" }
& 'C:\Program Files\Talky\talky.exe' migrate --data-dir 'D:\TalkyData'   # yalnızca ilk düğümde
Start-Service Talky
```

6. **Doğrula**, sonra bir sonraki düğüme geç:

```powershell
Invoke-WebRequest https://node-01.corp:8443/readyz -UseBasicParsing   # 200 olmalı
& 'C:\Program Files\Talky\talky.exe' doctor
```

Kurallar:

- **Veritabanı göçleri geriye uyumlu olmalıdır.** Yuvarlanan yükseltme sırasında eski ve yeni
  sürüm aynı şemaya aynı anda yazar. Sütun silme ve yeniden adlandırma iki sürüme yayılır:
  önce yaz-ve-oku, sonra bir sonraki sürümde temizle.
- Küme içinde **en fazla bir minör sürüm farkı** kabul edilir. Planlı (M2): mesh el
  sıkışması sürümü taşıyacak ve uyumsuzluğu loglayacak.
- MSI ile yükseltme `MajorUpgrade Schedule="afterInstallInitialize"` kullanır: eski sürüm
  önce kaldırılır, sonra yenisi kurulur. Servis önce `Stop-Service Talky` ile durdurulur
  (bölüm 4.1, "Bilinen açıklar"). `DataDir` klasörü ve `appsettings.json` yükseltmede
  silinmez; yeni servis, `HKLM\SOFTWARE\Talky` altında hatırlanan hesap ve `--data-dir` ile
  kaydedilir (laboratuvar VM'inde doğrulandı, bölüm 4.1). Kurtarma ayarı ve gecikmeli
  başlatma yükseltmede yeniden uygulanır. MSI servisi başlatmaz: `talky migrate` (ilk
  düğümde), sonra `Start-Service Talky`.

Geri alma: bir önceki yayın klasörünü `Install-Talky.ps1 -SourceDir <eski-yayın> -SkipMigrate`
ile geri kopyalayın (yapılandırma ve servis hesabı korunur; özel veri klasörüyle kurulmuş
düğümde `-DataDir D:\TalkyData` ekleyin) ya da MSI ile kurulmuş düğümde yeni sürümü
kaldırıp eskisini kurun:

```powershell
Stop-Service Talky
$p = Start-Process msiexec.exe -Wait -PassThru -ArgumentList '/x','Talky.Server-<yeni-sürüm>.msi','/qn','/norestart','/l*v','rollback-x.log'
if ($p.ExitCode -ne 0) { throw "msiexec /x exited with $($p.ExitCode); see rollback-x.log" }
$p = Start-Process msiexec.exe -Wait -PassThru -ArgumentList '/i','Talky.Server-<eski-sürüm>.msi','/qn','/norestart','/l*v','rollback-i.log'
if ($p.ExitCode -ne 0) { throw "msiexec /i exited with $($p.ExitCode); see rollback-i.log" }
Start-Service Talky
```

`msiexec /x` hatırlanan değerleri silmediği için eski sürüm aynı hesap ve klasörle kurulur
(laboratuvar VM'inde elle denendi). Eski sürüm hatırlama özelliğinden önceki bir sürümse
`SERVICE_ACCOUNT=` ve `DATADIR=` değerlerini bu komutta verin.

Şema geriye uyumlu olduğu için veri geri alma gerekmez.

---

## 7. Yedekleme ve geri yükleme

Yedeklenmesi gereken **iki** şey vardır. Üçüncüsü yoktur.

| Ne | Nasıl | Sıklık |
|---|---|---|
| SQL Server `Talky` veritabanı | SQL native backup (FULL + DIFF + LOG), sıkıştırılmış ve doğrulanmış | FULL haftalık, DIFF günlük, LOG 15 dk |
| Dosya payı `\\corp\dfs\talky\files` (planlı, M4) | Müşterinin standart dosya yedeklemesi veya DFS replikasyonu | Günlük |

Yedeklenmesi **gerekmeyenler**: Lucene.NET arama indeksi (planlı, M3; türetilmiş veridir,
SQL'den yeniden kurulur), loglar, `InstallDir` (MSI'den yeniden kurulur).

```sql
BACKUP DATABASE [Talky] TO DISK = N'\\yedek\sql\Talky_FULL.bak'
  WITH COMPRESSION, CHECKSUM, INIT, STATS = 5;
BACKUP LOG [Talky] TO DISK = N'\\yedek\sql\Talky_LOG.trn'
  WITH COMPRESSION, CHECKSUM, STATS = 5;
```

Geri yükleme sırası:

1. Bütün Talky düğümlerinde servisi durdurun (`Stop-Service Talky`).
2. Veritabanını geri yükleyin (`RESTORE DATABASE … WITH NORECOVERY`, log zinciri, sonra
   `WITH RECOVERY`).
3. Dosya payını aynı zaman noktasına geri yükleyin. **Pay ile veritabanı arasındaki zaman
   farkı önemlidir**: pay veritabanından eskiyse bazı mesajların eki bulunamaz. Bu yüzden
   pay yedeği veritabanı yedeğinden *sonra* alınmalı, geri yüklemede de aynı sıra
   korunmalıdır.
4. `search` düğümlerinde indeksi silin (`<DataDir>\index`) ve servisi başlatın; indeks
   yeniden kurulur.
5. Her düğümde `talky doctor`.

Kurtarma hedefleri: RPO 15 dakika (log yedeği aralığı), RTO 1 saat (tek veritabanı +
pay geri yükleme).

---

## 8. Log konumları

| Ne | Nerede |
|---|---|
| Talky düğümü (JSON, günlük döner, son 30 dosya tutulur — `Talky:Logging:RetainedFileCount`) | `<DataDir>\logs\talky<yyyyMMdd>.log`, ör. `talky20261124.log` |
| Talky uyarı ve hataları | Windows Olay Günlüğü → Application, kaynak `Talky` (her iki kurulum yolu da kaynağı oluşturur) |
| LiveKit (WinSW sarmalayıcı, 50 MB × 10) | `%ProgramData%\Talky\logs\livekit\` |
| MSI kurulumu | `msiexec /l*v <dosya>` ile verdiğiniz yol |
| Arama indeksi (log değil, veri) | `<DataDir>\index` |

Bir isteği izlemek için JSON log satırlarındaki izleme kimliğini (`@tr`) kullanın. Düğümler
arası izleme bugün yoktur; mesh M2'de gelir.

Servis başlarken (host ve logger daha yokken) biten hatalar ayrıca
`<DataDir>\logs\talky-startup.log` dosyasına ve Olay Günlüğü'ne (kaynak `Talky`) yazılır.
Elle çalıştırılan komutlar (`doctor`, `config validate`, `migrate`) bu ikisine yazmaz; hatayı
yalnızca konsola basar.

---

## 9. `talky doctor`

Her düğümde çalıştırılabilir; kurulumun son adımı olarak `Install-Talky.ps1` zaten
çalıştırır.

```powershell
& 'C:\Program Files\Talky\talky.exe' doctor --data-dir C:\ProgramData\Talky
```

Bugün var olan denetimler (tam liste):

| Denetim | Ne bakar |
|---|---|
| `config` | Bütün `Talky` seçenek bölümleri bağlanıp doğrulanabiliyor mu |
| `certificate` | Yapılandırılan TLS sertifikası bulunuyor mu, süresi geçmemiş mi, gerçek düğümde geliştirme sertifikası mı |
| `identity` | `doctor`'ın hangi hesapla çalıştırıldığını bildirir (bilgi amaçlı); servis hesabının kendisini sınamaz |
| `log-directory` | Log klasörü var mı / oluşturulabiliyor mu, bu hesap yazabiliyor mu |
| `os` | İşletim sistemi ve sürümü; istemci Windows sürümü (Windows 10/11) Development dışında uyarıdır ("yalnızca laboratuvar") |
| `runtime` | .NET çalışma zamanı ve sunucu GC açık mı |
| `sql` | Veritabanına erişim, beklenen ayarlar ve bekleyen göçler (bekleyen göç uyarıdır: `talky migrate`) |

Mesh eşleri ve `jobs` gecikmesi (M2), dosya payı (M4) ve LiveKit (M5) denetimleri ilgili
özellikle gelir; SQL'e göre saat kayması denetimi M2 için açık bir iştir. Disk alanı ve saat
kaynağı bugün yalnızca kurulum öncesinde `Test-TalkyPrerequisites.ps1` ile denetlenir. Çıkış kodu 0 sağlıklı, 0 dışı bir şey bozuk
demektir (yalnızca Error satırları çıkış kodunu değiştirir). Bir düğümü havuza almadan önce
`doctor` ve `/readyz` birlikte yeşil olmalıdır.

---

## 10. Sorun giderme

| Belirti | Muhtemel sebep | Bakılacak yer |
|---|---|---|
| Servis başlıyor, hemen duruyor | `appsettings.json` bozuk ya da zorunlu bir alan (ör. bağlantı dizesi) eksik/geçersiz | `<DataDir>\logs\talky-startup.log`, Olay Günlüğü (kaynak `Talky`), `talky config validate --data-dir <DataDir>` (bozuk JSON'da dosya adı ve hata ile çıkış kodu 3), ardından `talky doctor` |
| Servis başlamıyor, Olay Günlüğü "logon failure" | gMSA host'a kurulmamış ya da "Log on as a service" yok | `Test-ADServiceAccount -Identity talky`, GPO kullanıcı hakları |
| TLS el sıkışması başarısız, servis ayakta | gMSA sertifikanın özel anahtarını okuyamıyor | Bölüm 3, anahtar dosyası ACL'i |
| İstemci 401, tarayıcıda kimlik istemi | SPN eksik veya iki hesapta kayıtlı | `setspn -Q HTTP/talky.corp` |
| Yük dengeleyici bütün düğümleri "down" görüyor | Prob `/healthz` yerine `/readyz` olmalı ya da düğüm boşaltmada | `Invoke-WebRequest .../readyz` |
| (M2) Mesajlar geliyor ama gecikmeli, farklı düğümlerde | Mesh kurulmamış | Admin panelinde (M3) mesh eş durumu; `Test-NetConnection <eş> -Port 9443` |
| (M3) Aramada yeni mesajlar çıkmıyor | İndeksleme `jobs` gecikmesi ya da `search` düğümü kapalı | Admin panelinde `jobs` gecikmesi; `search` düğümünde `doctor` |
| Toplantıda bağlanıyor ama ses/görüntü yok | TURN aktarım aralığı (30000–40000/UDP) kapalı; ya da `node_ip` yanlış | `Get-NetFirewallRule -Group LiveKit`; `livekit.yaml` içinde `rtc.node_ip` |
| Toplantıya hiç katılınamıyor | 7880 proxy'si veya LiveKit servisi kapalı | `Get-Service LiveKit`, `%ProgramData%\Talky\logs\livekit\` |
| (M4) Dosya yüklenmiyor | Pay erişimi ya da AMSI karantinası | Pay denetimi (M4 ile `doctor`'a gelir); `jobs` tablosunda tarama işleri |
| Yeniden başlatmadan sonra herkes aynı anda bağlanıyor | `Reconnect{delayMs}` gönderilmeden servis durdurulmuş | Boşaltmayı bölüm 6'daki sıraya göre yapın |

Kaldırma:

```powershell
.\deploy\windows\Uninstall-Talky.ps1 -WhatIf
.\deploy\windows\Uninstall-Talky.ps1              # veri korunur
.\deploy\windows\Uninstall-Talky.ps1 -RemoveData  # yapılandırma, log ve indeks de silinir
```

MSI ile kurulmuş düğümde servisi önce durdurun (MSI'nin kendi durdurması boşaltmayı
beklemek için tasarlanmadı, bölüm 4.1 "Bilinen açıklar"). `msiexec /x` veri klasörünü ve
`HKLM\SOFTWARE\Talky` anahtarını bırakır; düğümü tamamen temizlerken ikisini de elle silin:

```powershell
Stop-Service Talky
$p = Start-Process msiexec.exe -Wait -PassThru -ArgumentList '/x','Talky.Server-<sürüm>.msi','/qn','/norestart','/l*v','uninstall.log'
if ($p.ExitCode -ne 0) { throw "msiexec /x exited with $($p.ExitCode); see uninstall.log" }
# Tamamen temizlemek için (yapılandırma ve loglar da gider):
#   Remove-Item -Recurse -Force 'D:\TalkyData'; Remove-Item HKLM:\SOFTWARE\Talky
```

Hiçbir durumda SQL veritabanı ve dosya payı silinmez; onlar elle kaldırılır.
