# Talky işletim kılavuzu

Windows Server 2022/2025 üzerinde Talky kurmak, yükseltmek, yedeklemek ve arızasını bulmak.
Mimari için `docs/architecture.md`, medya spike'ı için `docs/livekit-windows-spike.md`.

Bütün betikler `deploy/windows/` ve `deploy/livekit/` altındadır, PowerShell 5.1 uyumludur,
yükseltilmiş oturum ister ve **`-WhatIf` destekler**. Bir sunucuda ilk kez çalıştırmadan
önce her zaman `-WhatIf` ile çalıştırın.

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
sürümü, 8443/9443 portlarının boş olması, sertifikanın varlığı ve özel anahtarı, dosya
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
   `Talky:Tls:CertificateThumbprint` alanına yazar.

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

Betik sırayla: klasörleri açar, ACL'leri uygular (`InstallDir` üzerinde okuma+çalıştırma,
`DataDir`/`logs`/`index` üzerinde değiştirme), yayın çıktısını kopyalar,
`appsettings.json`'ı şablondan üretir, servisi **Automatic (Delayed Start)** olarak kaydeder,
kurtarma eylemlerini 5 sn / 10 sn / 30 sn olarak ayarlar, güvenlik duvarı kurallarını ekler,
servisi başlatır ve `talky doctor` çalıştırır.

MSI ile kurulum (kurumsal dağıtım araçları için):

```powershell
msiexec /i Talky.Server-<sürüm>.msi SERVICE_ACCOUNT="CORP\talky$" DATADIR="D:\TalkyData" /qn /l*v install.log
# MSI ACL uygulamaz; gMSA kullanılıyorsa hemen ardından:
.\deploy\windows\Install-Talky.ps1 -AclOnly -ServiceAccount 'CORP\talky$' -DataDir 'D:\TalkyData'
# ve %ProgramData%\Talky\appsettings.json içindeki yer tutucuları doldurun:
.\deploy\windows\Install-Talky.ps1 -ConfigOnly -DataDir 'D:\TalkyData' -SqlConnectionString '…' -CertificateThumbprint '…'
```

MSI, LocalSystem'e varsayılan verir ve gMSA'yı `SERVICE_ACCOUNT` ile alır; `SERVICE_PASSWORD`
her zaman boş kalır. Kurulumdan sonra kurtarma ayarlarını doğrulayın:

```powershell
sc.exe qfailure Talky      # RESET_PERIOD 86400, RESTART 5000/10000/30000 görmelisiniz
sc.exe qc Talky            # START_TYPE AUTO_START (DELAYED)
```

### 4.2 Üç düğümlü küme

Tipik yerleşim: iki `api` düğümü, bir `search` düğümü, ayrı SQL AG, ayrı LiveKit düğümü.

```powershell
# node-01 ve node-02 (api + worker)
.\deploy\windows\Install-Talky.ps1 `
    -ServiceAccount 'CORP\talky$' -Roles api,worker `
    -SqlConnectionString 'Server=talky-ag.corp;Database=Talky;Integrated Security=true;Encrypt=true' `
    -CertificateThumbprint <parmak-izi> `
    -MeshSeeds node-01.corp,node-02.corp,node-03.corp `
    -FileShare \\corp\dfs\talky\files

# node-03 (search)
.\deploy\windows\Install-Talky.ps1 `
    -ServiceAccount 'CORP\talky$' -Roles search `
    -SqlConnectionString 'Server=talky-ag.corp;Database=Talky;Integrated Security=true;Encrypt=true' `
    -CertificateThumbprint <parmak-izi> `
    -MeshSeeds node-01.corp,node-02.corp,node-03.corp `
    -DataDir D:\TalkyData          # indeks NVMe üzerinde olsun
```

`-MeshSeeds` her düğümde **aynı tam listeyi** içerir; düğüm kendini listeden eler. Mesh
kuralı yalnızca `-MeshSeeds` verildiğinde açılır ve varsayılan olarak yerel alt ağa
kısıtlıdır:

```powershell
.\deploy\windows\Set-TalkyFirewall.ps1 -MeshRemoteAddress 10.20.30.0/24
```

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
| 9443 | TCP | Talky düğümü | Yalnızca diğer Talky düğümleri |
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

---

## 6. Yuvarlanan yükseltme

Amaç: kullanıcı bir toplantıdan düşmez, mesaj kaybolmaz, yeniden bağlanma dalgası tek bir
düğüme yığılmaz.

Düğüm başına sıra (aynı anda **tek** düğüm):

1. **Boşalt.** Düğüme boşaltma sinyali verilir. `/readyz` derhal **503** dönmeye başlar;
   yük dengeleyici düğümü havuzdan çıkarır ve yeni bağlantı gelmez.
2. **İstemcileri nazikçe gönder.** Düğüm açık WebSocket oturumlarına `Reconnect{delayMs}`
   çerçevesi gönderir. İstemciler yeniden bağlanmayı `[0, delayMs]` aralığında rastgele
   geciktirir — 30 sn'lik pencere, tek bir düğüme yığılan sürü etkisini önler.
3. **30 saniye bekle.** Bu süre boyunca düğüm devam eden istekleri tamamlar; yeni istek
   almaz.
4. **Durdur, değiştir, başlat.**

```powershell
# 1-3: sürücü betiği yerine servis üzerinden en basit yol
Stop-Service Talky            # boşaltma koordinatörü 503 + Reconnect + 30 sn'yi kendi yürütür
.\deploy\windows\Install-Talky.ps1 -ServiceAccount 'CORP\talky$' -Roles api,worker `
    -SqlConnectionString '…' -CertificateThumbprint '…' -MeshSeeds …
# Install-Talky.ps1 servisi zaten durdurur, dosyaları değiştirir ve başlatır.
```

5. **Doğrula**, sonra bir sonraki düğüme geç:

```powershell
Invoke-WebRequest https://node-01.corp:8443/readyz -UseBasicParsing   # 200 olmalı
& 'C:\Program Files\Talky\talky.exe' doctor
```

Kurallar:

- **Veritabanı göçleri geriye uyumlu olmalıdır.** Yuvarlanan yükseltme sırasında eski ve yeni
  sürüm aynı şemaya aynı anda yazar. Sütun silme ve yeniden adlandırma iki sürüme yayılır:
  önce yaz-ve-oku, sonra bir sonraki sürümde temizle.
- Küme içinde **en fazla bir minör sürüm farkı** kabul edilir. Mesh el sıkışması sürümü
  taşır ve uyumsuzluğu loglar.
- MSI ile yükseltme `MajorUpgrade Schedule="afterInstallInitialize"` kullanır: eski sürüm
  önce kaldırılır, sonra yenisi kurulur — tek dosyalık host hiçbir zaman kullanımdayken
  değiştirilmez. `DataDir` ve `appsettings.json` yükseltmede korunur.

Geri alma: bir önceki MSI'yi kurun (`msiexec /x` sonra eski sürümü `/i`) veya bir önceki
yayın klasörünü `Install-Talky.ps1 -SourceDir` ile geri kopyalayın. Şema geriye uyumlu
olduğu için veri geri alma gerekmez.

---

## 7. Yedekleme ve geri yükleme

Yedeklenmesi gereken **iki** şey vardır. Üçüncüsü yoktur.

| Ne | Nasıl | Sıklık |
|---|---|---|
| SQL Server `Talky` veritabanı | SQL native backup (FULL + DIFF + LOG), sıkıştırılmış ve doğrulanmış | FULL haftalık, DIFF günlük, LOG 15 dk |
| Dosya payı `\\corp\dfs\talky\files` | Müşterinin standart dosya yedeklemesi veya DFS replikasyonu | Günlük |

Yedeklenmesi **gerekmeyenler**: Lucene.NET arama indeksi (türetilmiş veridir, SQL'den
yeniden kurulur), loglar, `InstallDir` (MSI'den yeniden kurulur).

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
| Talky düğümü (JSON, günlük döner, 31 dosya) | `%ProgramData%\Talky\logs\talky-<tarih>.log` |
| Talky uyarı ve hataları | Windows Olay Günlüğü → Application, kaynak `Talky` |
| LiveKit (WinSW sarmalayıcı, 50 MB × 10) | `%ProgramData%\Talky\logs\livekit\` |
| MSI kurulumu | `msiexec /l*v <dosya>` ile verdiğiniz yol |
| Arama indeksi (log değil, veri) | `<DataDir>\index` |

Bir olayı düğümler arasında izlemek için log satırlarındaki `traceId` ve `conversationId`
alanlarını kullanın; her ikisi de mesh üzerinden taşınır.

---

## 9. `talky doctor`

Her düğümde çalıştırılabilir; kurulumun son adımı olarak `Install-Talky.ps1` zaten
çalıştırır.

```powershell
& 'C:\Program Files\Talky\talky.exe' doctor --data-dir C:\ProgramData\Talky
```

Denetlediği başlıklar: yapılandırmada kalan yer tutucular, SQL bağlantısı ve şema sürümü,
TLS sertifikasının varlığı/geçerliliği/özel anahtarı, dosya payına yazma, LiveKit
düğümlerine erişim, mesh eşlerinin durumu, `jobs` gecikmesi, disk alanı, saat kayması.
Çıkış kodu 0 sağlıklı, 0 dışı bir şey bozuk demektir. Bir düğümü havuza almadan önce
`doctor` ve `/readyz` birlikte yeşil olmalıdır.

---

## 10. Sorun giderme

| Belirti | Muhtemel sebep | Bakılacak yer |
|---|---|---|
| Servis başlıyor, hemen duruyor | `appsettings.json` içinde yer tutucu kalmış veya JSON bozuk | `%ProgramData%\Talky\logs`, ardından `talky doctor` |
| Servis başlamıyor, Olay Günlüğü "logon failure" | gMSA host'a kurulmamış ya da "Log on as a service" yok | `Test-ADServiceAccount -Identity talky`, GPO kullanıcı hakları |
| TLS el sıkışması başarısız, servis ayakta | gMSA sertifikanın özel anahtarını okuyamıyor | Bölüm 3, anahtar dosyası ACL'i |
| İstemci 401, tarayıcıda kimlik istemi | SPN eksik veya iki hesapta kayıtlı | `setspn -Q HTTP/talky.corp` |
| Yük dengeleyici bütün düğümleri "down" görüyor | Prob `/healthz` yerine `/readyz` olmalı ya da düğüm boşaltmada | `Invoke-WebRequest .../readyz` |
| Mesajlar geliyor ama gecikmeli, farklı düğümlerde | Mesh kurulmamış (9443 kapalı veya `MeshSeeds` eksik) | Panelde mesh eş durumu; `Test-NetConnection <eş> -Port 9443` |
| Aramada yeni mesajlar çıkmıyor | İndeksleme `jobs` gecikmesi ya da `search` düğümü kapalı | Panelde `jobs` gecikmesi; `search` düğümünde `doctor` |
| Toplantıda bağlanıyor ama ses/görüntü yok | TURN aktarım aralığı (30000–40000/UDP) kapalı; ya da `node_ip` yanlış | `Get-NetFirewallRule -Group LiveKit`; `livekit.yaml` içinde `rtc.node_ip` |
| Toplantıya hiç katılınamıyor | 7880 proxy'si veya LiveKit servisi kapalı | `Get-Service LiveKit`, `%ProgramData%\Talky\logs\livekit\` |
| Dosya yüklenmiyor | Pay erişimi ya da AMSI karantinası | `talky doctor` pay denetimi; `jobs` tablosunda tarama işleri |
| Yeniden başlatmadan sonra herkes aynı anda bağlanıyor | `Reconnect{delayMs}` gönderilmeden servis durdurulmuş | Boşaltmayı bölüm 6'daki sıraya göre yapın |

Kaldırma:

```powershell
.\deploy\windows\Uninstall-Talky.ps1 -WhatIf
.\deploy\windows\Uninstall-Talky.ps1              # veri korunur
.\deploy\windows\Uninstall-Talky.ps1 -RemoveData  # yapılandırma, log ve indeks de silinir
```

Hiçbir durumda SQL veritabanı ve dosya payı silinmez; onlar elle kaldırılır.
