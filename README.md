# Yankı

> **Durum (2026-11-25): yayın öncesi, ürün olarak henüz kullanılamaz.** Bugün yalnızca M0 iskeleti vardır:
> `yanki.exe` host'u ve CLI (`serve`, `migrate`, `doctor`, `config validate`, `version`), `/healthz`, `/readyz`,
> `/api/v1/ping`, SQL şeması v0 (`yanki migrate`), `/ws` el sıkışması (`Hello`/`Welcome`, `Ping`/`Pong`, drain,
> bağlantı bütçesi), `@yanki/core` WS istemcisi, MSI ve kurulum betikleri, LiveKit medya düğümü kurulum betikleri.
> Kimlik doğrulama yoktur (M1'e kadar her `Hello` reddedilir); mesajlaşma, arama, dosyalar, medya, push, yönetim
> paneli ve kullanıcı arayüzü **planlıdır** (`docs/plan-v1.md` §9). Aşağıdaki ürün tanımı ve üç ürünlük ayak izi
> v1 hedefidir; LiveKit'in Windows'ta mı yoksa Hyper-V cihazında mı çalışacağı Spike A ölçümüne bağlı olarak
> açıktır (`docs/livekit-windows-spike.md`). Ayrıntılı durum: `docs/plan-v1.md`, "M0 durumu".

**English summary.** Yanki is an on-premises enterprise communication platform in the style of Teams /
Skype for Business (messaging, 1:1 calls, meetings with screen sharing, file sharing) for Windows Server, built
as a single .NET 10 binary plus SQL Server and LiveKit. It is pre-release: only the M0 skeleton exists (host and
CLI, SQL schema v0, the WebSocket handshake, installers), there is no sign-in and no messaging yet, and it is
not usable as a product. The documentation under `docs/` and this README are in Turkish; code, comments and
log messages are in English. Security reports: see `SECURITY.md`.

Kurumsal, tamamen on-prem çalışan Teams / Skype for Business tarzı iletişim platformu:
mesajlaşma, 1:1 arama, grup toplantısı + ekran paylaşımı, dosya paylaşımı.

**Öncelikler:** çok hızlı · hatasız · mümkün olduğunca az ayrı ürün.

Hedeflenen üretim ayak izi yalnızca üç üründür: `yanki.exe` (.NET 10, Windows servisi), **SQL Server 2025**, **LiveKit**.
Ayrıntılı mimari ve kilometre taşları için `docs/` klasörüne bakın.

## Depo yapısı

```
server/     .NET 10 çözümü (Yanki.slnx). Tek binary yanki.exe; roller: api | worker | search | push-relay | all
api/        Sözleşmeler: proto (WS çerçeveleri, küme mesh'i), altın fixture'lar; OpenAPI üretimi M1'de başlar
clients/    pnpm + Turborepo. packages/{protocol,core,...}; apps/{web,desktop,mobile} planlı (M3/M4/M6)
deploy/     Windows kurulum (MSI, PowerShell), LiveKit yapılandırması
docs/       Mimari, protokol, işletim
```

## Geliştirme ortamı (Windows 11)

Gerekenler: .NET SDK en az 10.0.400 (`global.json` alt sınırdır, `rollForward: latestMajor` ile daha yeni ana sürüme geçer; hedef çerçeve `net10.0` kalır), Node ≥ 22, pnpm 11, SQL Server 2025 LocalDB (`sqllocaldb start MSSQLLocalDB`).
Rust yalnızca masaüstü (Tauri) için, `clients/apps/desktop` üzerinde çalışırken gerekir.

```bash
# Sunucu
cd server
dotnet build Yanki.slnx
dotnet test Yanki.slnx

# İstemciler
cd clients
pnpm install
pnpm gen          # api/proto -> TypeScript (packages/protocol/src/gen)
pnpm typecheck
```

`dotnet add package` yerine sürümleri doğrudan `server/Directory.Packages.props` dosyasına yazın (merkezi paket yönetimi).

## Sözleşme kuralları

- `api/proto/**` tek gerçek kaynaktır; C# `Yanki.Protocol` projesinde build sırasında, TS `@yanki/protocol` paketinde `pnpm gen` ile üretilir ve commit'lenir.
- Yalnızca ekleme yapılır; alan numarası asla yeniden kullanılmaz. `buf lint` ve `buf breaking` bunu denetler; ikisi de CI iş akışında (`.github/workflows/ci.yml`) tanımlıdır, ancak iş akışı henüz hiç çalışmadı.
- Wire kodlaması v1'de proto3 JSON'dur; `oneof body` alan adı çerçeve tipidir.

## Katkı kuralları (kısa)

- `TreatWarningsAsErrors` açıktır; analizör uyarısı bırakılmaz.
- Sıcak yollarda (mesaj gönderimi, geçmiş, sync, fan-out) her değişiklik ölçümle gelir.
- Doğruluk hiçbir zaman küme bus'ına dayanmaz; SQL'deki `seq` ve istemci resync'i esastır.
