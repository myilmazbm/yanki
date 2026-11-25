# Spike A — LiveKit'i Windows Server üzerinde çalıştırmak

**Soru:** LiveKit SFU, üretim ayak izine ayrı bir Linux ürünü eklemeden doğrudan Windows
Server 2022/2025 üzerinde, hedef yükte kabul edilebilir kalitede çalışır mı?

**Neden önemli:** Ürün vaadi "mümkün olduğunca az ayrı ürün". Cevap evetse üretim ayak izi
üç üründe kalır (`talky.exe`, SQL Server 2025, LiveKit). Hayırsa medya, Hyper-V üzerinde
mühürlü bir Linux cihaza taşınır (`deploy/hyperv/`) ve müşteri yine yalnızca zaten sahip
olduğu hipervizörü yönetir.

**Durum:** Geliştirme makinesinde ön doğrulama (§1) ve 2026-11-25'te Windows Server 2025
laboratuvar VM'inde kurulum + işlev denetimi (§1.2), ardından bağımsız bir incelemenin
bulgularıyla ikinci tur ve LiveKit 1.13.11 (§1.3) yapıldı. Spike A'nın asıl konusu olan
gerçek sunucu donanımında yük ölçümü (§2–§4) **açık**.

---

## 1. Ön doğrulama (2026-09-05, Windows 11 geliştirme makinesi)

| Adım | Sonuç |
|---|---|
| `livekit_1.13.6_windows_amd64.zip` indirildi (18.673.013 bayt) | SHA-256 `9df299b6…4a9475`, sürümün `checksums.txt` dosyasıyla **eşleşti** |
| `livekit-server.exe` (55.777.280 bayt), `--version` | `livekit-server version 1.13.6` |
| `--dev --bind 127.0.0.1` ile ~10 sn çalıştırıldı | Süreç ayakta; TCP 7880 dinliyor, `GET /` → 200 OK; TCP 7881 dinliyor; UDP 7882 bağlı |
| `deploy/livekit/livekit.yaml` (yer tutucular doldurulmuş) ile çalıştırıldı | Kabul edildi; TURN başladı (3478/UDP, 5349/TLS), `node_ip` açıkça kullanıldı |
| `lk 2.18.6` ile 30 sn'lik yerel yük testi (1 yayıncı, 4 abone) | 4/4 abone bağlandı, **%0 paket kaybı**, 1.3 Mbps/abone |

Windows üzerinde LiveKit **çalışıyor**. Ölçülmemiş olan tek şey, gerçek sunucu donanımında
gerçek yükte CPU ve kayıp davranışı — Spike A'nın asıl konusu bu.

### 1.1 Ön doğrulamadan çıkan iki kalıcı bulgu

**a) LiveKit'in kendi kapasite yönetimi Windows'ta devre dışı.** Başlangıçta:

```
ERROR livekit hwstats/cpu_null.go:38
  CPU monitoring unsupported on current platform. Server capacity management will be disabled
```

Sonuç: `livekit_node_cpu_load` benzeri düğüm yükü metriği ve LiveKit'in kendi yük tabanlı
kabul kontrolü yok. Bu, onaylanmış tasarımı **bozmuyor** — oda yerleşimini zaten Talky
backend'i yapıyor, LiveKit değil — ama backend'in düğüm yükünü LiveKit'e soramayacağı
anlamına geliyor. Yük, düğümün kendi performans sayaçlarından okunmalı
(`Invoke-LiveKitLoadTest.ps1` bunu `Win32_PerfFormattedData_*` üzerinden yapıyor).
Linux cihaz alternatifinde bu sınırlama yok.

**b) Gömülü TURN, her aktarım tahsisi için ayrı bir UDP portu açıyor.**
`turn.relay_range_start`–`relay_range_end`, varsayılan **30000–40000/UDP**. Bu aralık
güvenlik duvarında açık değilse TURN el sıkışması başarılı olur ama medya akmaz — klasik
"bağlandı, ses yok" arızası. Aralık artık `livekit.yaml` içinde açıkça sabitlendi ve
`Install-LiveKitService.ps1` kuralı otomatik ekliyor.

### 1.2 Windows Server 2025 laboratuvar VM'i (2026-11-25)

**Bu bir işlev denetimidir, Spike A ölçümü değildir.** Makine 2 vCPU / 4 GB'lık bir
Hyper-V laboratuvar VM'idir (Windows Server 2025 Datacenter, etki alanı üyesi, özel bir
düğüm IP'si, kısa ömürlü öz-imzalı TURN sertifikası) ve yük üreteci SFU'nun kendisinde
çalıştı. Aşağıdaki CPU sayıları yalnızca "kurulum çalışıyor ve medya akıyor" demektir; §3
ölçütleri için §2'deki donanım ve ayrı yük üreteci gerekir.

Önce betikler `main`'deki halleriyle elle çalıştırıldı (ekip lideri):

| Adım | Sonuç |
|---|---|
| `Get-LiveKit.ps1 -Version 1.13.6` | 17,8 MB indirildi, `checksums.txt`'e karşı SHA-256 doğrulandı, 32 sn; ikinci çalıştırma "already current" |
| `Install-LiveKitService.ps1` (LocalSystem) | WinSW 2.12.0 SHA-256 doğrulandı; servis Running, gecikmeli otomatik başlatma, `sc qfailure` 5000/10000/30000 ms, sıfırlama 86400 sn; `livekit-server.exe` önceliği High; TCP 7880/7881/5349 dinliyor, UDP 3478 ve `<düğüm-IP>`:7882 bağlı; `GET /` → 200 "OK"; "LiveKit" grubunda altı kural |
| Çalışırken ikinci kurulum | 3,5 sn'de yeniden oluşturuldu, Running |
| `livekit-server.exe` öldürüldü | 5,6 sn sonra yeniden yanıt veriyor; `Stop-Service` 0,3 sn |
| Elle oda + `lk load-test` (1 yayıncı, 4 abone, 30 sn) | 4/4 iz, abone başına 1,3 Mbps, %0 kayıp; `livekit-server` CPU ort. %1,1, tepe tek çekirdeğin %11'i |

Bu çalıştırma şu hataları buldu; hepsi düzeltildi:

- `-ServiceAccount 'NT AUTHORITY\NetworkService'` ile servis birkaç saniyede bir çöküyordu
  (System 7031, Application `.NET Runtime 1026 … UnauthorizedAccessException …
  livekit-service.err.log`), kurulum betiği ise "started" deyip 0 ile çıkıyordu. Sebep:
  önceki LocalSystem çalıştırmasının oluşturduğu log dosyalarına NetworkService
  yazamıyordu. İlk kez NetworkService ile yapılan kurulumda (dosyaları servis kendisi
  oluşturduğu için) sorun çıkmadı; yani sorun yalnızca hesap değişiminde görülür. Artık
  betik log klasörüne Modify, `livekit.yaml` ve TURN dosyalarına okuma veriyor ve servisi
  ancak 8 sn sonra hâlâ aynı süreçle ayaktaysa başlamış sayıyor (`docs/operations.md` §4.2).
- Çöken sarmalayıcı `livekit-server.exe`'yi sahipsiz bırakıyordu (servis Stopped iken 7880
  yanıt veriyordu). Yeniden kurulum, `-Remove` ve başarısız başlatma artık bunları
  durduruyor.
- `Invoke-LiveKitLoadTest.ps1` odayı oluşturmuyordu (`room.auto_create: false` → her
  katılımcı `not found`) ve 5 katılımcının 0'ı bağlanmışken CPU'ya bakıp PASS diyordu.
  Artık odayı oluşturuyor, `lk`'nın abone özet tablosunu okuyor ve bağlantı/iz/kayıp
  denetimlerinden biri kalırsa FAIL ile 1 döndürüyor.
- LiveKit logunu stdout'a değil **stderr**'e yazıyor (`livekit-service.err.log`); şablonun
  webhook bloğu M5'e kadar kapatıldı.

Ardından yeni `deploy/livekit/Test-LiveKitInstall.ps1` aynı VM'de temiz bir sıfırlamadan
sonra koşuldu; **tüm denetimler PASS, çıkış 0, toplam 144 sn**:

| Adım | Ölçülen |
|---|---|
| LocalSystem kurulumu + yukarıdaki tüm servis/port/kural denetimleri | PASS; `livekit.yaml` devralmasız, Users/Everyone erişimi yok |
| Çalışırken ikinci kurulum | Yeni sarmalayıcı süreci, aynı denetimler PASS |
| `livekit-server.exe` öldürüldü | 5,9 sn'de geri geldi ve yanıt verdi (sınır 20 sn); sahipsiz süreç yok |
| `Invoke-LiveKitLoadTest.ps1` 1 yayıncı / 4 abone / 30 sn | 4/4 iz, abone başına 1,3 Mbps (toplam 5,1 Mbps), %0 kayıp; ana bilgisayar CPU ort. %4,2, tepe %5,9; `livekit-server` ort. %1,6, tepe tek çekirdeğin %4,7'si. Ağ sayaçları 0: trafik loopback üzerinden aktı |
| Negatif: oda oluşturulmadan yük testi | Beklendiği gibi FAIL, çıkış 1 ("could not connect … not found"), CPU değerlendirilmedi |
| LocalSystem'in üzerine NetworkService ile yeniden kurulum | 45 sn sonra aynı süreç Running, iki süreç de `NT AUTHORITY\NETWORK SERVICE`, `GET /` 200, System'de LiveKit için 7031 yok, Application'da `.NET Runtime` hatası yok |
| `-Remove` | Servis, kurallar ve `livekit-server.exe` yok |

Bir önceki tam koşu (aynı gün) de yeşildi: toparlanma 5,9 sn, CPU tepe %8,9.
Hatanın gerçekten yakalandığı da ayrıca denendi: LocalSystem kurulumundan sonra
`livekit-service.err.log` NetworkService'e kapatılıp NetworkService kurulumu yapıldığında
betiğin son hali yaklaşık 57 sn'de "did not stay up" ile hata verdi (8 sn bekleme, ardından
servisin en az 35 sn Stopped kalması beklenir). Servis kayıtlı ama Stopped kaldı, sahipsiz
`livekit-server.exe` durduruldu, betik döndükten sonra 60 sn boyunca yeni 7031 olmadı ve
LiveKit süreci kalmadı.

### 1.3 İkinci tur: bağımsız inceleme bulguları ve LiveKit 1.13.11 (2026-11-25)

Aynı laboratuvar VM'inde, bağımsız bir incelemenin bulguları önce düzeltilmemiş betiklerle
denendi, sonra düzeltmeyle yeniden koşuldu.

| Deneme | Düzeltmeden önce | Düzeltmeden sonra |
|---|---|---|
| `Invoke-LiveKitLoadTest.ps1` 8 yayıncı / 2 abone / 30 sn, sağlıklı düğüm | Abone başına `6/8`, toplam `12/16`, %0 kayıp, ama "every subscriber received its tracks" FAIL, CPU değerlendirilmedi, çıkış 1. Sebep: `lk`'nın varsayılan "speaker" düzeni her aboneyi en çok 6 katılımcıya abone eder; Spike A'nın varsayılan profili (8 yayıncı) hiç geçemezdi | Beklenen abone başına `min(yayıncı, 6)`: PASS, toplam 12/12, `livekit-server.exe` 6 örneğin 6'sında görüldü, CPU tepe %12,1, çıkış 0 |
| 1.13.6, `turn.cert_file` olmayan bir dosya, kurulum betiği (2 kez) | İkisinde de 61 sn sonra ham `WaitForStatus … Time out has expired`; servis yalnızca ~40 ms Running kaldı ve normal biçimde durdu (7031 yok, yeniden başlatma yok); sebep tek satır olarak `livekit-service.out.log`'da: `TURN tls cert required: open C:\nope\missing.pem: …` | İki koşuda da betik "did not stay up" ile hata verdi ve `out.log`'u gösterdi (o sürüm kodunda ~106 sn: 60 sn `WaitForStatus` + 8 sn + 35 sn sessizlik; bekleme sonra 30 sn'ye indirildi); servis Stopped, 7031 yok, kurtarma eylemleri yerinde |
| 1.13.11, aynı bozuk sertifika yolu (3 kez) | Üçünde de SCM döngüsü sürdü: her 30 sn'de bir 7031, servis her seferinde ~45 ms Running; betik dönüşünden sonra 3–4 yeni 7031 ve döngü sonsuz. Betik 1. koşuda "restart loop was ended" dedi (500 ms'lik yoklama hiçbir Running anını yakalamadı), 2. koşuda ham zaman aşımıyla düştü, 3. koşuda "did not stay Stopped within 140 s" uyarısının ardından yine "restart loop was ended" dedi | Üç koşuda da: betik kurtarma eylemlerini kaldırdı, önceden kuyruğa girmiş tek bir yeniden başlatma yine geldi (7034 "no corrective action" ya da yakalanıp düzgün durduruldu), servis 35 sn Stopped kaldıktan sonra "no restart pending" diyerek hata verdi (~114 sn); dönüşten sonra 100 sn boyunca 7031/7034 yok, LiveKit süreci yok, `sc qfailure` yine 5/10/30 sn |

Sürümler arasındaki fark: 1.13.6 bir başlangıç hatasından sonra 0 koduyla çıkar (WinSW bunu
normal durma olarak bildirir); 1.13.11 1 koduyla çıkar ve SCM'nin 5/10/30 sn kurtarma
merdiveni devreye girer, son adım sonsuza dek tekrarlanır. İkisinde de sebep JSON logda
değil, `livekit-service.out.log`'daki düz satırdadır. Düzeltilmiş betikte kurtarma eylemleri
beklerken kaldırılıp sonra geri yüklendiği için ikisinde de servis kayıtlı ve Stopped kalır;
son kod (30 sn başlatma sınırı) 1.13.11'de 84 sn'de hata verdi.

Ek olarak denendi: `-InstallDir 'C:/Program Files/LiveKit'` (eğik çizgiyle), göreli TURN
yolları (`cert_file: turn-cert.pem`, yorumlu ve tırnaklı `key_file`) ve betik `C:\`'den
başlatılınca NetworkService kurulumu sağlıklı başladı; dosyalar `InstallDir` altında bulundu,
anahtar yalnızca SYSTEM, Administrators ve servis hesabına açık kaldı (Program Files'ın verdiği
Users ve uygulama paketi okumaları silindi).

**Sürümler ve tam kabul koşuları.** `Get-LiveKit.ps1` artık varsayılan olarak `latest`'i değil,
tam kabul koşusunu geçen en yeni sürümü kurar. Düzeltilmiş betiklerle, temiz sıfırlamadan sonra:

| Sürüm | Sonuç | Notlar |
|---|---|---|
| 1.13.6 | 102 PASS, 0 FAIL, çıkış 0, 158 sn | Toparlanma 5,8 sn; 4/4 iz, 1,3 Mbps/abone, %0 kayıp; CPU tepe %6,0 (değerlendirilmedi, `-SkipCpuCheck`) |
| 1.13.11 (yeni varsayılan) | İki koşu, ikisi de 102 PASS, 0 FAIL, çıkış 0, 158 sn | Toparlanma 5,9 sn; 4/4 iz, 1,3 Mbps/abone, %0 kayıp; CPU tepe %9,4 (değerlendirilmedi) |

Farklar: başlangıç hatasında çıkış kodu (yukarıda). Bunun dışında kabul testinin gördüğü
davranış aynı. Bir ara koşuda (`stopparentprocessfirst` henüz true iken) 1.13.11 düğümünde bir
`stopwait`, WinSW 2.12.0'ın `ProcessHelper.GetChildren` içinde çıkmış sürecin `StartTime`'ını
okurken "The handle is invalid" ile çökmesine yol açtı; durdurma 7031 ve SCM yeniden
başlatmasına döndü. 132 ayrı durdurma/başlatma döngüsünde (120 boşta, 12 yük testinin hemen
ardından) yeniden üretilemedi; bu yüzden düzeltme kaynak koda dayanır: `stopparentprocessfirst`
false yapıldı, böylece alt süreç listesi LiveKit henüz çalışırken alınır (LiveKit alt süreç
başlatmaz; Ctrl+C ile boşaltma iki ayarda da aynı). Yukarıdaki üç kabul koşusu bu ayarla yapıldı.

---

## 2. Spike A kurulumu (gerçek donanım)

| Rol | Makine | Notlar |
|---|---|---|
| SFU | Windows Server 2022/2025, 16 vCPU / 32 GB / 10 GbE | Yalnız LiveKit; başka rol yok |
| Yük üreteci | Ayrı makine (Windows veya Linux), aynı L2/L3 | SFU'nun CPU'sunu kirletmemek için **asla** SFU üzerinde çalıştırılmaz |
| Kısıtlı istemci | UDP'si tamamen engellenmiş bir VLAN'daki bir iş istasyonu | TURN/TLS geri dönüşünü ölçmek için |

Kurulum:

```powershell
# SFU düğümünde, yükseltilmiş oturum
.\deploy\livekit\Get-LiveKit.ps1    # varsayılan: kabul testini geçen en yeni sürüm (§1.3)
# Şablonu C:\Program Files\LiveKit\livekit.yaml olarak kopyalar, servisi başlatmaz
.\deploy\livekit\Install-LiveKitService.ps1 -SignalRemoteAddress <api-düğümü-CIDR> -NoStart
# O kopyadaki "<...>" yer tutucuları doldurun (node_ip, turn.domain, sertifika, keys);
# depodaki deploy\livekit\livekit.yaml şablon olarak doldurulmamış kalır
.\deploy\livekit\Install-LiveKitService.ps1 -SignalRemoteAddress <api-düğümü-CIDR>
```

Ölçüm (yük üreteci makinesinde):

```powershell
$env:LIVEKIT_API_SECRET = '<secret>'
.\deploy\livekit\Invoke-LiveKitLoadTest.ps1 `
    -Url ws://media-01.talky.corp:7880 -ApiKey talky `
    -NodeComputerName media-01 `
    -Room spike -VideoPublishers 8 -Subscribers 100 -Duration 10m
```

Betik `artifacts/livekit-spike/` altına bir CSV (5 sn'lik örneklerle toplam CPU,
`livekit-server` süreç CPU'su ve çalışma kümesi, ağ Mbps) ve `lk` çıktısının tamamını
(abone başına bit hızı ve paket kaybı tabloları) yazar.

---

## 3. Geçme / kalma ölçütleri

Dördü de sağlanmalıdır. Herhangi biri kalırsa Spike A başarısızdır.

| # | Ölçüt | Eşik | Kaynak |
|---|---|---|---|
| 1 | SFU ana bilgisayar CPU'su | 10 dakikalık koşunun **tepe** değeri **< %60** | `Invoke-LiveKitLoadTest.ps1` CSV'si, `TotalCpuPercent` |
| 2 | Bildirilen paket kaybı | Her abone için **< %1** (toplamda da) | `lk` özet tablosu, "Total Pkt. Loss" |
| 3 | Katılım başarısı | 108 katılımcının **tamamı** odaya girer; `lk` hata sayısı 0 | `lk` özet tablosu, "Error" sütunu |
| 4 | UDP engelli istemcinin TURN/TLS'e düşmesi | İlk medyaya kadar **< 3 sn** | Kısıtlı istemcide tarayıcı `getStats()` / `chrome://webrtc-internals`, ICE `relay` adayı seçilene kadar geçen süre |

Neden bu eşikler:

- **%60 CPU**, tek bir toplantı zirvesinin düğümü doyurmaması için baş boşluğu bırakır;
  10 GbE'de 8 yayıncı / 100 abone, 16 vCPU'luk bir düğümün hedef çalışma noktasıdır.
- **%1 kayıp** üzerinde Opus ve VP8/H.264 gizleme duyulur/görülür bozulmaya döner.
- **3 sn**, kullanıcının "bağlanıyor" ekranında kabul ettiği süredir; TURN/TLS'e geçiş
  kullanıcı için görünmez olmalıdır.

Ek olarak, geçse bile kaydedilecek gözlemler: `livekit-server` çalışma kümesi, ağ
tepe Mbps, ve koşu sırasında Windows Olay Günlüğü'nde SFU kaynaklı herhangi bir hata.

---

## 4. Sonuçlar

Spike A koşulduğunda doldurulacak.

| Ölçüt | Eşik | Ölçülen | Geçti / Kaldı |
|---|---|---|---|
| Tepe ana bilgisayar CPU'su | < %60 | | |
| Paket kaybı (en kötü abone) | < %1 | | |
| Katılım başarısı | 108/108, 0 hata | | |
| TURN/TLS geri dönüş süresi | < 3 sn | | |

Koşu bilgisi: tarih, LiveKit sürümü, `lk` sürümü, SFU donanımı, CSV dosya adı.

---

## 5. Başarısızlık durumunda: Hyper-V medya cihazı

Dört ölçütten biri bile kalırsa medya `deploy/hyperv/` altındaki Linux cihaza taşınır:

- `New-MediaAppliance.ps1` — Hyper-V üzerinde Generation 2 VM (16 vCPU / 32 GB, statik
  bellek; medya için dinamik bellek ve denetim noktaları kapalı). **İskelet durumunda**;
  temel imaj indirme ve cloud-init seed ISO üretimi `TODO(M1)` olarak işaretli.
- `cloud-init/user-data` — Ubuntu LTS + resmî LiveKit sürümü (sürüm sabitli, sha256
  doğrulamalı), `livekit.service` systemd birimi, ufw ile yalnızca medya portları,
  parolasız/anahtar tabanlı break-glass erişim. Cihaz mühürlüdür: elle yamalanmaz,
  yeniden kurulur.

Bu yol ayak izini dört ürüne çıkarmaz; **aynı LiveKit** ürününü farklı bir işletim
sisteminde çalıştırır. Müşteri açısından değişen tek şey, medya düğümünün bir Windows
servisi yerine bir Hyper-V VM'i olmasıdır.

**Karar kuralı:** Spike A geçerse Windows'ta kalınır ve `deploy/hyperv/` iskelet olarak
donar. Kalırsa `deploy/hyperv/` tamamlanır ve `deploy/livekit/` yalnızca laboratuvar
kurulumları için tutulur. Karar M1'in başında, tek seferde verilir; iki yol paralel
sürdürülmez.

---

## 6. Port özeti

Her iki yolda da aynı portlar gerekir.

| Port | Protokol | Kim erişir | Not |
|---|---|---|---|
| 7880 | TCP | Yalnız Talky API düğümleri | Sinyalleşme; istemciler `https://talky.corp/media/{node}/rtc` üzerinden YARP ile ulaşır |
| 7881 | TCP | İstemciler | UDP engelli istemciler için medya |
| 7882 | UDP | İstemciler | Birincil medya (SRTP/ICE), tüm eşler tek portta çoğullanır |
| 3478 | UDP | İstemciler | Gömülü TURN |
| 5349 | TCP | İstemciler | TURN over TLS |
| 30000–40000 | UDP | İstemciler | TURN aktarım tahsisleri — **unutulursa medya akmaz** |

---

## 7. Tedarik zinciri notu

`Get-LiveKit.ps1` ve `Invoke-LiveKitLoadTest.ps1`, LiveKit ve `lk` sürümlerini yayının
`checksums.txt` dosyasına karşı doğrular. **WinSW bir `checksums.txt` yayımlamıyor ve
`WinSW-x64.exe` Authenticode ile imzalı değil** (2.12.0'da doğrulandı:
`Get-AuthenticodeSignature` → `NotSigned`). Bu yüzden beklenen SHA-256,
`Install-LiveKitService.ps1` içinde `-WinSwSha256` varsayılanı olarak sabitlenmiştir
(2.12.0 için `05b82d46…b3a0da`) ve indirmeden sonra doğrulanır. Sürüm yükseltilirken bu
hash elle doğrulanıp güncellenmelidir.
