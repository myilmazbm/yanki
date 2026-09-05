# Talky — çalışma kuralları (Claude Code için)

Talky, tamamen on-prem çalışan kurumsal Teams/Skype tarzı iletişim platformudur. Öncelik sırası:
**çok hızlı → hatasız → az ayrı ürün (yönetilebilirlik)**. Kullanıcıyla Türkçe yazışılır; kod, tanımlayıcılar,
yorumlar ve log mesajları İngilizcedir.

## Kilitli mimari kararlar (değiştirmeden önce kullanıcıya sor)
- Backend **.NET 10 / C#**, tek binary `talky.exe`, Windows servisi (gMSA), roller `api|worker|search|push-relay|all`.
- Üretim yalnızca **Windows Server**. Üretim ürünleri **tam olarak üç**: `talky.exe`, **SQL Server 2025**, **LiveKit**.
  NATS/Redis/MinIO/coturn/Caddy/Prometheus/Grafana/Sentry/Elasticsearch **eklenmez**; işlev binary'ye veya SQL'e taşınır.
- Node'lar arası yayın: binary içinde gRPC tam-mesh (`api/proto/talky/cluster/v1/mesh.proto`). **Doğruluk bus'a dayanmaz**;
  SQL'deki konuşma başına `seq` ve istemcinin resync'i esastır.
- Arama: gömülü Lucene.NET (`--role search`); dosyalar SMB/DFS (`IBlobStore`); virüs tarama Windows Defender AMSI.
- İstemciler: React web + Tauri 2 masaüstü (Windows öncelikli) + React Native/Expo mobil; ortak TS `@talky/core`.
- Ölçek 10k–50k kullanıcı; çok-forest AD v1 (Negotiate/Kerberos + ADFS OIDC + LDAP bind); federasyon ve SAML v1.1.
- Tam plan: `docs/plan-v1.md`. Protokol kuralları: `docs/protocol.md` (normatif).

## Derleme / test
```bash
cd server && timeout 480 dotnet build Talky.slnx -m:1 -p:NodeReuse=false     # uyarılar hatadır
cd server && dotnet test Talky.slnx -m:1 -p:NodeReuse=false
cd clients && pnpm install && pnpm gen && pnpm typecheck && pnpm lint && pnpm test
```
- **`dotnet add package` kullanma** (bu ortamda merkezi paket yönetimiyle asılı kalıyor). csproj'a `<PackageReference Include="X" />`,
  `server/Directory.Packages.props`'a `<PackageVersion Include="X" Version="…" />` yaz. En son kararlı sürüm:
  `node -e "fetch('https://api.nuget.org/v3-flatcontainer/<küçük-harf-id>/index.json').then(r=>r.json()).then(j=>console.log(j.versions.filter(v=>!v.includes('-')).pop()))"`
- `dotnet build` çok düğümlü modda asılı kalabiliyor; her zaman `-m:1 -p:NodeReuse=false` ve `timeout` kullan.
- pnpm 11: build script izinleri `clients/pnpm-workspace.yaml` → `allowBuilds`. TypeScript **5.9'da sabit** (7.x'e geçme).
- SQL: `(localdb)\MSSQLLocalDB` (SQL Server 2025 Express LocalDB); `sqllocaldb start MSSQLLocalDB`.
- Proto değişince: `pnpm gen` (TS, commit'lenir) + .NET build (C#, build sırasında üretilir). `buf lint` temiz olmalı.

## Paralel çalışma (ekip lideri modeli)
- Bağımsız iş paketleri Opus ajanlarına, her biri **kendi git worktree'sinde** (`../talky-wt/<paket>`, dal `wp/<paket>`) verilir;
  ajanlar ana checkout'a dokunmaz. Lider dalları `main`'e merge eder, derleme/test/inceleme yapar.
- Dosya sahipliği listeleri çakışmayı önler; `Directory.Packages.props` yalnızca eklemeyle değiştirilir.
- Commit mesajı sonu: `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>` (lider) / `Claude Opus 5` (ajanlar).

## Kod standartları
- `TreatWarningsAsErrors`, analizörler açık; `CancellationToken` her yerde; zaman için `TimeProvider`; `async void` yok; sync-over-async yok.
- Sıcak yollar (gönderim, geçmiş, sync, fan-out, arama) ölçümsüz değiştirilmez; hedefler `docs/plan-v1.md` §6.
- Token/parola/kişisel veri loglanmaz. Kullanıcıya görünen metinler i18n'e gider (tr kaynak, en takip).
