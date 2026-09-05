# Spike A — LiveKit'i Windows Server üzerinde çalıştırmak

**Soru:** LiveKit SFU, üretim ayak izine ayrı bir Linux ürünü eklemeden doğrudan Windows
Server 2022/2025 üzerinde, hedef yükte kabul edilebilir kalitede çalışır mı?

**Neden önemli:** Ürün vaadi "mümkün olduğunca az ayrı ürün". Cevap evetse üretim ayak izi
üç üründe kalır (`talky.exe`, SQL Server 2025, LiveKit). Hayırsa medya, Hyper-V üzerinde
mühürlü bir Linux cihaza taşınır (`deploy/hyperv/`) ve müşteri yine yalnızca zaten sahip
olduğu hipervizörü yönetir.

**Durum:** Geliştirme makinesinde ön doğrulama yapıldı (aşağıda). Gerçek Windows Server
donanımında ölçüm bekliyor.

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
.\deploy\livekit\Get-LiveKit.ps1 -Version 1.13.6
# livekit.yaml içindeki "<...>" yer tutucuları doldurun (node_ip, turn.domain, sertifika, keys)
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
