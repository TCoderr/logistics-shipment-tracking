# Logistics & Shipment Tracking System (LST)

Tek bir lojistik işletmesi için gönderi operasyonu ve takip sistemi: gönderi oluşturma, hub taramaları, teslimat denemeleri, istisna
kuyruğu, simüle carrier entegrasyonu, alıcı için public takip ve canlı güncellenen operasyon paneli.

- Backend: ASP.NET Core (.NET 10) Minimal API, modular monolith, EF Core + Npgsql, PostgreSQL 18
- Arayüz: React 19 + TypeScript + Vite, API ile **aynı origin**; canlı güncelleme SignalR
- Tasarım belgeleri: [`docs/01–09`](docs/) · kararlar: [`docs/adr/`](docs/adr/) · ilerleme ve kanıtlar:
  [`docs/implementation-progress.md`](docs/implementation-progress.md), [`docs/final-verification.md`](docs/final-verification.md)

Proje sahibi: TCoderr

> Bu depo yerel bir demo/portföy uygulamasıdır. Testlerin geçmesi üretim dağıtımı veya trafik kapasitesi sertifikası değildir; yük testi
> yapılmamıştır ve kapasite iddiası yoktur. Kapsam dışı: çok kiracılı SaaS, rota optimizasyonu, GPS/harita, kısmi teslimat, teslim sonrası
> müşteri iadesi, gerçek carrier/ödeme/bildirim, AI/LLM özelliği.

## Mimari

```
src/Lst.Api                  Host: composition, auth pipeline, health, SPA barındırma, SignalR hub'ı (Realtime/), bakım komutları
src/Lst.Platform             Tek LogisticsDbContext + migration'lar, idempotency, audit, outbox, rate limit, retention, hub referans verisi
src/Lst.Modules.Identity     Identity + cookie + antiforgery; her istekte güncel DB rolü/hub ataması
src/Lst.Modules.Shipments    Gönderi domain'i: durum makinesi, tarama/teslimat/carrier kararları (saf politikalar), liste, detay, public takip
src/Lst.Modules.Operations   İstisna kuyruğu, SLA izleyici, KPI özeti, audit okuma
src/Lst.Modules.Tracking     Carrier webhook (HMAC), kalıcı inbox + işleyici, SIMEX simülatörü (yalnız Development)
src/web                      React SPA (operasyon paneli + mobil public takip), bileşen testleri, Playwright E2E
tests/Lst.UnitTests          Saf domain/politika ve mimari sınır testleri
tests/Lst.IntegrationTests   Gerçek PostgreSQL 18 (Testcontainers) + WebApplicationFactory
scripts/                     Yerel yığın, duman testi, migration doğrulaması, lisans bildirimi, kaynak parmak izi
```

Modüller tek veritabanında ayrı şemalar kullanır (`shipments`, `operations`, `tracking`, `identity`, `platform`). Broker, Redis, MediatR,
generic repository, event sourcing ve AutoMapper yoktur.

### Bir gönderinin akışı

1. **Oluşturma** (`POST /api/shipments`, OpsAgent/OpsManager): `Idempotency-Key` zorunlu. Gönderi + taraflar + koliler + `Created` olayı + audit +
   outbox mesajı tek transaction'da yazılır. Aynı anahtar + aynı gövde ilk yanıtı **byte düzeyinde aynen** döner; farklı gövde `422`.
2. **Hub taramaları** (`POST /api/hubs/{hub}/scans`, yalnız o hub'a atanmış HubOperator): varış → çıkış → varış hub'ında dağıtıma çıkış.
   Kararı saf `ScanPolicy` verir (Applied / Duplicate / Stale / Rejected); kolilerin hepsi hub'da değilken çıkış reddedilir.
3. **Teslimat denemesi** (idempotent): teslim edildi ya da başarısız (neden zorunlu). Başarısızlık istisna açar; 3 deneme sonunda hak biter.
4. **İstisna kuyruğu**: görevli üzerine alır ve çözer (ek deneme onayı, göndericiye iade, işlem gerekmiyor …). Durum değiştiren çözüm,
   gönderi komutuyla aynı transaction'dadır.
5. **Carrier olayları**: imzalı webhook → kalıcı makbuz (`202`) → inbox işleyicisi (lease + fencing) → Applied / Duplicate / Stale /
   Rejected / PendingReview. Aynı kimlik farklı içerik = çakışma kaydı + istisna.
6. **Outbox**: durum/istisna değişiklikleri commit sonrası dağıtılır (simüle bildirim kaydı + canlı panel ipucu).
7. **Public takip** (`/t/{kod}`): alıcı oturumsuz, yalnız allowlist alanlarını görür (ad, adres, telefon, hub kodu yok).

Eşzamanlılık: operatör komutları `If-Match` (ETag = satır sürümü) ister; durum değiştiren her komut gönderi satırını kilitler.

## Ön koşullar

- .NET SDK 10.0.4xx (`global.json`: 10.0.401) · `dotnet tool restore` (dotnet-ef)
- Docker Desktop (PostgreSQL 18 konteyneri, Testcontainers, Compose yığını)
- Node.js ≥ 22.12 (geliştirme ve testler için; image içindeki build kendi Node'unu kullanır)
- E2E için Chrome ya da `npx playwright install chromium`

## Yerelde çalıştırma (önerilen: Docker Compose, HTTPS)

```powershell
# 1) Bir kez: rastgele secret'lar, geliştirici sertifikası dışa aktarımı ve demo kullanıcı parolaları üretilir (.local/ altında, git dışı)
.\scripts\local-stack.ps1 init -SeedDemo

# 2) Image'ı derle ve yığını başlat (PostgreSQL 18 + tek seferlik migrator + HTTPS API + arayüz). -Simulation: carrier simülatörü
.\scripts\local-stack.ps1 up -SeedDemo -Simulation

# 3) Tarayıcı: https://localhost:8443   (geliştirici sertifikası güvenilir değilse bir kez: dotnet dev-certs https --trust)

.\scripts\local-stack.ps1 status
.\scripts\local-stack.ps1 down            # durdurur; veriler kalır
.\scripts\local-stack.ps1 down -Purge     # yalnız bu projenin volume'larını da siler
```

Linux/macOS/Git Bash eşdeğeri: `bash scripts/ci-e2e-stack.sh .local lst 8443`.

### Demo kullanıcıları ve parolalar

Depoda **parola yoktur** ve hiçbir komut parolayı ekrana yazmaz. `init -SeedDemo` her kullanıcı için rastgele bir parola üretir ve
`.local/seed/Seed__Passwords__<kullanıcı>` dosyasına yazar; parolayı bu dosyadan okursunuz (ya da dosyayı kendi parolanızla **yığını ilk kez
başlatmadan önce** değiştirirsiniz: en az 12 karakter, büyük/küçük harf, rakam, sembol).

| Kullanıcı | Rol | Hub | Arayüzde görebildiği |
|---|---|---|---|
| `ops.agent` | Operasyon görevlisi | — | Gönderiler, oluşturma, işlemler, istisnalar, özet |
| `ops.manager` | Operasyon yöneticisi | — | + takip kodu yenileme, audit, carrier simülasyonu (Demo) |
| `hub.ist` / `hub.ank` / `hub.izm` | Hub görevlisi | IST-01 / ANK-02 / IZM-01 | Tarama, kendi hub kapsamındaki gönderiler |

Demo verisi yalnız `Development` ortamında ve açık istekle (`-SeedDemo`) oluşturulur; demo gönderi yoktur, gönderileri arayüzden oluşturursunuz.
Simülatör paneli yalnız `Development` + `-Simulation` + yönetici rolüyle görünür; Production'da route yoktur.

### Örnek demo turu

`ops.manager` ile gönderi oluşturun → detayda **Carrier ata** (SIMEX) → **Carrier simülasyonu** `HappyPath` → durum yenilemeden "Teslim edildi"ye
ilerler. Ya da ikinci bir tarayıcıda `hub.ist` ile **Tarama** ekranından koli etiketini okutun; ilk tarayıcıdaki liste canlı güncellenir.
Alıcı görünümü: detaydaki **Public takip sayfasını aç** bağlantısı.

## Geliştirme (API + Vite)

```powershell
dotnet tool restore
dotnet dev-certs https --trust
# Bağlantı dizesi ve demo parolaları user-secrets'ta tutulur (repoda gizli bilgi yoktur):
dotnet user-secrets --project src/Lst.Api set "ConnectionStrings:Lst" "Host=localhost;Port=5432;Database=lst_dev;Username=<kullanıcı>;Password=<parola>"
dotnet user-secrets --project src/Lst.Api set "Seed:Passwords:ops.agent" "<parola>"      # diğer kullanıcılar için de
dotnet run --project src/Lst.Api --launch-profile https                                  # https://localhost:7210 (Development: migration + seed)

cd src/web
npm ci
npm run dev                                                                              # http://localhost:5173, /api ve /realtime API'ye proxy'lenir
```

Vite proxy'si cookie ve CSRF başlıklarını değiştirmeden taşır; cookie'ler `__Host-` önekli ve `Secure` olduğundan dev sunucusu Chromium/Firefox'ta
`http://localhost` üzerinden çalışır. Derlenmiş arayüzü API'den sunmak için: `npm run build` ve `Spa:DistPath=../web/dist`.

Kimlik akışı: `GET /api/auth/csrf` → `POST /api/auth/login` (`X-CSRF-TOKEN`) → `GET /api/auth/me` (kullanıcıya bağlı yeni token) → durum değiştiren her
istekte `X-CSRF-TOKEN`. Elle deneme: [`requests/shipments.http`](requests/shipments.http).

## Testler ve doğrulama

```powershell
dotnet build Lst.slnx -c Release
dotnet test --project tests/Lst.UnitTests/Lst.UnitTests.csproj -c Release
dotnet test --project tests/Lst.IntegrationTests/Lst.IntegrationTests.csproj -c Release   # gerçek PostgreSQL 18 (Testcontainers); Docker yoksa HATA verir, atlanmaz

cd src/web
npm run typecheck; npm run lint; npm run test; npm run build

# E2E: ayrı, izole yığın (ayrı proje adı, volume ve secret'lar) + gerçek tarayıcı
..\..\scripts\local-stack.ps1 init -LocalDir .local-e2e -SeedDemo
..\..\scripts\local-stack.ps1 up -Project lst-e2e -LocalDir .local-e2e -Port 18443 -SeedDemo -Simulation
$env:LST_E2E_CHANNEL = "chrome"; npm run e2e
..\..\scripts\local-stack.ps1 down -Project lst-e2e -LocalDir .local-e2e -Purge
```

Diğer doğrulama betikleri (depo kökünden):

| Betik | Ne yapar |
|---|---|
| `python scripts/stack_smoke.py --project <p> --local-dir <dir> --port <port>` | HTTPS cookie/CSRF create → replay → detay; çalışma zamanı rolünün audit'i değiştirememesi; API yeniden oluşturma; DB kesintisi/iyileşme |
| `python scripts/migration_upgrade_check.py` | Boş DB kurulumu + önceki dilim şemalarından verili yükseltme (tek kullanımlık konteyner) |
| `python scripts/third_party_notices.py` | `THIRD-PARTY-NOTICES.md` ve arayüzle dağıtılan lisans metinlerini yeniden üretir |
| `scripts/source-fingerprint.ps1` | Kaynak durumunun parmak izi (git yokken sonuçların hangi kaynağa ait olduğunu kaydetmek için) |

CI: [`.github/workflows/ci.yml`](.github/workflows/ci.yml) — `backend`, `web`, `e2e` işleri (yalnız `contents: read`, action'lar commit SHA ile sabit).
**Bu workflow uzak depoda çalıştırılmamıştır**; yereldeki eşdeğer komutların sonuçları `docs/final-verification.md`'dedir.

## Migration

Üretimde uygulama başlarken otomatik migration **yapılmaz**. Migration'ı tablo sahibi rol uygular; uygulama, yalnız
[`db/runtime-role-grants.sql`](db/runtime-role-grants.sql) yetkilerine sahip **ayrı** bir rolle bağlanır (DDL yok; audit tablosunda yalnız `SELECT, INSERT`).

```powershell
dotnet ef migrations has-pending-model-changes -p src/Lst.Platform -s src/Lst.Api
dotnet ef migrations script --idempotent -p src/Lst.Platform -s src/Lst.Api -o migrate.sql   # incelenip sahip rolle uygulanır
# Compose yığınında aynı iş: tek seferlik "migrator" servisi (image'ın --migrate komutu)
```

## Hata, yeniden deneme ve saklama davranışı

| Durum | Davranış |
|---|---|
| Yanıt kayboldu (create / teslimat denemesi) | Aynı `Idempotency-Key` ile tekrar: ilk yanıt aynen döner, ikinci kayıt oluşmaz. Arayüz aynı gövdeyi aynı anahtarla yeniden gönderir; alan değişirse yeni işlem sayar ve uyarır |
| Eski ekran (`If-Match` uyuşmadı) | `412`; arayüz güncel durumu yükler, yazılan metni korur |
| Carrier olayı tekrar / sırasız / çelişkili | Duplicate / Stale (zaman çizelgesinde görünür, durumu geri sarmaz) / PendingReview veya çakışma → istisna |
| Inbox işleyicisi çöktü | Lease dolunca başka işçi devralır; eski işçi sonucu yazamaz (fencing). Geçici DB hatası deneme bütçesini tüketmez; kalıcı hata 5 denemede dead-letter |
| Outbox teslimi başarısız | Geri çekilerek yeniden dener; 8 denemede dead-letter. Yönetici `GET /api/operations/outbox`, `POST …/{id}/replay` (audit'li) |
| Canlı bağlantı koptu | Şerit "yeniden bağlanılıyor / veri bayat olabilir" der; bağlanınca etkin sorgular sunucudan yeniden okunur (mesaj kaçsa da veri doğrudur) |
| Veritabanı kesintisi | `/health/live` 200, `/health/ready` 503; iyileşince kendiliğinden toparlanır |
| Saklama (demo varsayımı, **yasal uygunluk kanıtı değildir**) | 180 günü geçmiş tamamlanmış gönderilerde PII maskelenir; ham carrier payload'ı 30 gün; işlenmiş outbox 7 gün. Elle: `POST /api/operations/retention/run` |

## Bilinen sınırlar

- **Tek instance:** rate limit sayaçları, SignalR grupları ve bağlantı kaydı bellek içidir (backplane/Redis yok).
- **Canlı kanal** yalnız durum ve istisna aç/ata/çöz değişikliklerinde ipucu üretir; diğer değişiklikler 60 sn'lik mutabakat veya elle yenilemeyle görünür.
  Yetkisi kaldırılan açık bağlantının iptali anlık değildir (varsayılan ≤ 15 sn).
- **Durumsuz cookie:** çıkış sunucuda bileti iptal etmez; kopyalanmış bir cookie süresi dolana kadar (8 saat, kayan) geçerlidir.
- Kullanıcı/rol/hub ataması yönetim ekranı ve OIDC yoktur; demo kullanıcıları seed ile gelir.
- `return-to-sender` nedeni doğrulanır ama saklanmaz (serbest metin audit'e yazılmaz).
- Carrier yalnız simülasyondur (SIMEX); gerçek dış servis çağrısı ve gerçek bildirim gönderimi yoktur.
- Değişiklik akışı (`changes?after=`) bilinçli olarak yoktur ([ADR-020](docs/adr/ADR-020-realtime-invalidation-and-resync.md)).
- Performans/kapasite ölçülmedi.

## Lisans

Proje için henüz bir lisans seçilmedi. Dağıtılan üçüncü taraf bileşenler ve lisansları: [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).
