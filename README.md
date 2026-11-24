# Talky

Kurumsal, tamamen on-prem çalışan Teams / Skype for Business tarzı iletişim platformu:
mesajlaşma, 1:1 arama, grup toplantısı + ekran paylaşımı, dosya paylaşımı.

**Öncelikler:** çok hızlı · hatasız · mümkün olduğunca az ayrı ürün.

Üretim ayak izi yalnızca üç üründür: `talky.exe` (.NET 10, Windows servisi), **SQL Server 2025**, **LiveKit**.
Ayrıntılı mimari ve kilometre taşları için `docs/` klasörüne bakın.

## Depo yapısı

```
server/     .NET 10 çözümü (Talky.slnx). Tek binary talky.exe; roller: api | worker | search | push-relay | all
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
dotnet build Talky.slnx
dotnet test Talky.slnx

# İstemciler
cd clients
pnpm install
pnpm gen          # api/proto -> TypeScript (packages/protocol/src/gen)
pnpm typecheck
```

`dotnet add package` yerine sürümleri doğrudan `server/Directory.Packages.props` dosyasına yazın (merkezi paket yönetimi).

## Sözleşme kuralları

- `api/proto/**` tek gerçek kaynaktır; C# `Talky.Protocol` projesinde build sırasında, TS `@talky/protocol` paketinde `pnpm gen` ile üretilir ve commit'lenir.
- Yalnızca ekleme yapılır; alan numarası asla yeniden kullanılmaz. `buf lint` ve `buf breaking` CI'da zorunludur.
- Wire kodlaması v1'de proto3 JSON'dur; `oneof body` alan adı çerçeve tipidir.

## Katkı kuralları (kısa)

- `TreatWarningsAsErrors` açıktır; analizör uyarısı bırakılmaz.
- Sıcak yollarda (mesaj gönderimi, geçmiş, sync, fan-out) her değişiklik ölçümle gelir.
- Doğruluk hiçbir zaman küme bus'ına dayanmaz; SQL'deki `seq` ve istemci resync'i esastır.
