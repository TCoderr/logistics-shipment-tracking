# Uygulama İlerleme Kaydı (Dilim 2–10)

> Oturumlar arası devam kaydı. Durumlar: Pending / InProgress / ImplementedUnverified / Verified / Blocked.
> Komut çıktıları `docs/verification/` altında, redakte edilmiş halde. Secret, cookie, CSRF token, PII yazılmaz.
> Proje sahibi: TCoderr.

## Kaynak baseline (2026-10-10)

| Konu | Değer |
|---|---|
| VCS | Git deposu **yok** (`git init` yapılmadı; görev yetkisi yok). Kaynak durumu `scripts/source-fingerprint.ps1` ile izlenir. |
| Baseline parmak izi | `files=96 sha256=e160f9aeae3e9e8e91bda4afc8ab1bd9c98bb83eabdd1f3cd70dfdfc9103f36b` (fingerprint betiği dahil) |
| Baseline doğrulama (bu oturum) | Release build 0 uyarı/0 hata; unit 58/58; integration 29/29 (gerçek PostgreSQL 18, Testcontainers), skip 0 — `docs/verification/baseline-slice1.log` |
| .NET | SDK 10.0.401, runtime 10.0.12 (`C:\Program Files\dotnet`) |
| Docker | Docker Desktop 4.94.0, Engine 29.8.2 (CLI: `%LOCALAPPDATA%\Programs\DockerDesktop\resources\bin`); başlangıçta container/volume yok |
| Node | v24.18.0, npm 12.1.0, pnpm/yarn mevcut |
| Tarayıcı | Chrome ve Edge kurulu; Playwright tarayıcı önbelleği boş |
| AGENTS.md / CLAUDE.md | Yok |

### 28 → 29 integration test farkı (Dilim 1 doğrulaması)
Önceki doğrulamada iki test eklendiği raporlandı ama toplam 28→29 oldu. Açıklama (bu oturumda kaynak üzerinden doğrulandı):
`AuthAndCsrfTests.Login_failure_is_generic_for_unknown_user_and_wrong_password` testi **yeniden adlandırılıp genişletildi**
(`Login_failure_is_generic_and_audited_without_credentials`) ve yeni olarak yalnız `Lockout_returns_the_same_generic_response_and_is_audited`
eklendi. Net artış +1'dir; hiçbir test silinmedi.

## Dilim durumu

| Dilim | Durum | Özet |
|---|---|---|
| 2 Docker, local stack, CI | Verified (remote CI koşusu yok) | Dockerfile, Compose, migrator, HTTPS, DP anahtarları, CI workflow; duman 20/20 |
| 3 Liste/arama/sayfalama | Verified | `GET /api/shipments`; DP korumalı keyset cursor; SQL'de hub kapsamı; unit 72, integration 48 |
| 4 Hub tarama, durum makinesi, iptal | Verified | ScanPolicy.Decide, aggregate kilidi, tarama + iptal endpoint'leri; unit 91, integration 64 (2 koşu) |
| 5 Teslimat denemesi, istisna, iade | Verified | Deneme (idempotent), Operations modülü (istisna kuyruğu), iade; unit 104, integration 74 (2 koşu), Docker build |
| 6 Carrier webhook, inbox, simülatör | Verified | Tracking modülü, imzalı webhook, kalıcı inbox, lease+fencing, DLQ, simülatör; unit 150, integration 91 (2 koşu), Docker build |
| 7 Outbox, SLA, KPI | Verified | Transactional outbox (interceptor), dağıtıcı lease+fencing, dead-letter, audit'li replay, SLA izleyici, KPI özeti, audit okuma; unit 161, integration 101 (2 koşu), Docker build |
| 8 Public tracking, rate limit, retention | Verified | Allowlist DTO, aynı 404, public başlıklar, log redaksiyonu, IP + güvenilir proxy rate limit, kod yenileme, saklama işi (tombstone); unit 169, integration 107 (2 koşu), Docker build |
| 9 React operasyon paneli + mobil public UI | Verified (remote CI koşusu yok) | React SPA aynı origin; 8 ekran; bileşen testi 38, E2E 21 (gerçek Chrome, izole yığın, boş DB), axe ihlali 0; unit 169, integration 125 (2 koşu) |
| 10 SignalR ve reconnect | Verified (remote CI koşusu yok) | Kimlikli SignalR hub'ı, sunucu yönetimli abonelik + yeniden doğrulama/iptal, outbox'tan PII'siz ipucu, (yeniden) bağlanmada HTTP eşitleme; unit 169, integration 133 (2 koşu), web 45, E2E 28 |

## Kesinti sonrası devam (2026-10-10, ikinci oturum)

Önceki oturum Dilim 8 kaydını yazdıktan hemen sonra (yerel 02:57) kesildi. Bu oturumda **önce durum tespit edildi**, sonra devam edildi.

| Tespit | Sonuç |
|---|---|
| Ana prompt dosyası (`Logistics_Dilim_02_10_Ana_Prompt.txt`) | Diskte **yok**; görev metni önceki oturum kaydından okundu (Dilim 2–10, bölüm A–F) |
| AGENTS.md / CLAUDE.md | Yok |
| VCS | Git deposu yok (init yapılmadı) |
| `docs/final-verification.md` | Yoktu (final doğrulama yapılmamıştı) |
| Dilim 9/10 kaynakları (`src/web`, SignalR hub) | Yoktu → başlanmamış |
| Proje süreçleri | Çalışan API/test/worker süreci yok (ilgisiz bir `node` süreci vardı; dokunulmadı) |
| Docker | Engine **kapalıydı** (Docker Desktop çalışmıyordu); kurulu uygulama başlatıldı, ayar/kurulum değişikliği yok. Container/volume/proje ağı yok; image'lar: `lst-api:local`, `postgres:18.6`, .NET sdk/aspnet, ryuk |
| Yarım dosya | Yok; en son kaynak değişikliği 02:54:53, Dilim 8 gate'i 02:55–02:56 (kaynaktan sonra) |
| Parmak izi | Kayıtlı `dedba615…` ≠ şimdiki `8d9ae0f5…` (ikisi de 196 dosya). Fark açıklaması: kayıtlı değer, ilerleme dosyasına **yazılmadan önce** hesaplandı ve ilerleme dosyası parmak izine dahildir (kendine referans). Bu yüzden eski sonuç otomatik geçerli sayılmadı; gate yeniden koşuldu |

Mevcut kaynak üzerinde yeniden doğrulama (Dilim 1–8 regresyonu) — `docs/verification/resume-baseline.log`:

| Kontrol | Komut | Exit | Sonuç |
|---|---|---|---|
| Araç + kilitli restore | `dotnet tool restore`; `dotnet restore Lst.slnx --locked-mode` | 0 / 0 | kilit hatası 0 |
| Release build | `dotnet build Lst.slnx -c Release --no-restore` | 0 | 0 uyarı / 0 hata |
| Pending model | `dotnet ef migrations has-pending-model-changes -p src/Lst.Platform -s src/Lst.Api --no-build --configuration Release` | 0 | değişiklik yok |
| Unit | `dotnet test --project tests/Lst.UnitTests/Lst.UnitTests.csproj -c Release --no-build` | 0 | 169/169, fail 0, skip 0 |
| Integration (PostgreSQL 18.6, Testcontainers) | `dotnet test --project tests/Lst.IntegrationTests/Lst.IntegrationTests.csproj -c Release --no-build` | 0 | 107/107, fail 0, skip 0 |

Dilim 1–10 durum özeti (bu oturumun başında):

| Dilim | Sınıf |
|---|---|
| 1–8 | Uygulanmış ve **mevcut kaynakta yeniden doğrulanmış** (build + unit + integration). Dilim 2 Compose/HTTPS dumanı ve Docker build bu noktada yeniden koşulmadı → final doğrulamada koşulacak |
| 9 | Başlanmamış → bu oturumda uygulanıyor |
| 10 | Başlanmamış |
| Final (F) | Başlanmamış |

## Görev kaynakları ve güvenli cleanup

| Kaynak | Sahiplik | Cleanup |
|---|---|---|
| Testcontainers konteynerleri (`postgres:18.6`, `testcontainers/ryuk`) | Her test koşusu kendi oluşturur | Ryuk otomatik siler |
| Compose projeleri `lst-smoke`, `lst-e2e`, `lst-ci`, `lst-final` (container, ağ, `*_pgdata`, `*_dpkeys`) | Duman / E2E / final doğrulama | `local-stack.ps1 down -Project <ad> -LocalDir <dizin> -Purge` — **hepsi silindi** |
| `.local-smoke/`, `.local-e2e/`, `.local-ci/`, `.local-final/` (rastgele secret'lar, PFX dışa aktarımı, demo parolaları) | Aynı; git dışı | **Silindi** (final cleanup) |
| Image `lst-api:local` ve taban image'lar | Compose build | **Bırakıldı** (önbellek); silmek için `docker image rm lst-api:local` |

---

## Dilim 2 — Docker, local stack, CI

Kapsam ve kabul (prompt): multi-stage Dockerfile (non-root), `.dockerignore`, API + PostgreSQL 18 Compose, one-shot migrator,
HTTPS (harici salt-okunur sertifika), kalıcı DB + Data Protection anahtar volume'ları, runtime/migration rol ayrımı,
Development-only açık opt-in seed (özel parola girişi), health live/ready, GitHub Actions workflow (minimal izin, pinlenmiş action'lar, lockfile),
MTP test raporları.
Kabul: mevcut testler pass/no skip; image build + izole Compose healthy; HTTPS cookie/CSRF create→replay→detail smoke;
API yeniden oluşturmada veri + auth cookie korunur; runtime audit yıkıcı işlemleri reddedilir; DB kesintisinde live 200 / ready 503,
iyileşince ready sağlıklı; remote CI çalıştırılmadıysa açıkça belirtilir.

### Dilim 2 sonucu — Verified (remote CI çalıştırılmadı)

Kaynak: `files=118 sha256=8262c1ea53118bdf2af74f1b30e2bf7579e7dcfc625a83804bf1ac09bcc73bf8` (scripts/source-fingerprint.ps1).

Değişen/eklenen dosyalar: `Dockerfile`, `.dockerignore`, `compose.yaml`, `deploy/postgres/init/01-roles.sh`, `scripts/local-stack.ps1`,
`scripts/stack_smoke.py`, `scripts/source-fingerprint.ps1`, `.github/workflows/ci.yml`, `.gitattributes`, `.gitignore`,
`Directory.Build.props` (kilit dosyaları), `*/packages.lock.json`, `src/Lst.Api/Program.cs`, `src/Lst.Api/Hosting/{HostConfiguration,DemoSeeder,MaintenanceCli}.cs`,
`src/Lst.Api/DevelopmentStartup.cs`, `src/Lst.Platform/Persistence/RuntimeGrants.cs` (+ gömülü SQL), test fixture (üretim migrate/grant kodu, `postgres:18.6`),
`tests/Lst.UnitTests/RuntimeGrantsTests.cs`, `docs/adr/ADR-013-local-stack-secrets-and-ci.md`.
Migration/contract değişikliği: yok. Demo kullanıcılarına `hub.ank` (ANK-02) eklendi.

| Kontrol | Komut | Exit | Sonuç | Log |
|---|---|---|---|---|
| Kilitli restore | `dotnet restore Lst.slnx --locked-mode` | 0 | 7 kilit dosyası | — |
| Release build | `dotnet build Lst.slnx -c Release --no-restore` | 0 | 0 uyarı / 0 hata | — |
| Unit | `dotnet test --project tests/Lst.UnitTests/Lst.UnitTests.csproj -c Release --no-build` | 0 | 64/64, skip 0 | slice2-tests.log (58 o an; +6 sonra) |
| Integration | `dotnet test --project tests/Lst.IntegrationTests/Lst.IntegrationTests.csproj -c Release --no-build` | 0 | 29/29, skip 0 | docs/verification/slice2-tests.log |
| Pending model | `dotnet ef migrations has-pending-model-changes … --no-build --configuration Release` | 0 | değişiklik yok | — |
| Migration script | `dotnet ef migrations script --idempotent … -o artifacts/migrations/migrate.sql` | 0 | üretildi | artifacts/ (git dışı) |
| Image + Compose | `scripts/local-stack.ps1 up -Project lst-smoke -LocalDir .local-smoke -Port 18443 -SeedDemo` | 0 | postgres/api healthy, migrator exit 0, 5 demo kullanıcı | docs/verification/slice2-stack-up.log |
| Yığın duman | `python scripts/stack_smoke.py --project lst-smoke --local-dir .local-smoke --port 18443` | 0 | 20/20 (CSRF→login→create→byte replay→detail ETag; runtime rol UPDATE/DELETE/TRUNCATE reddi; API recreate sonrası cookie+veri; DB kesintisi live 200/ready 503; iyileşme ready 200) | docs/verification/slice2-stack-smoke.log |
| Secret sızıntısı | image user/env/history/log taraması | — | UID 1654; env/katman/logda secret yok | — |
| Remote CI | — | — | **Çalıştırılmadı** (workflow hazır, YAML geçerli; remote/publish yetkisi yok) | — |

Bulunan ve düzeltilen hatalar: bakım host'u ortam adını yalnız `DOTNET_ENVIRONMENT`'tan okuyordu; Development'ta DI doğrulaması routing istiyordu;
Npgsql GSS kütüphanesi uyarısı; duman betiği recreate'te portu geçirmiyordu.
---

## Dilim 3 — Liste, arama, filtre, sayfalama

Kapsam: `GET /api/shipments` (docs/05 §4.3). Arama `q`: tracking no / paket etiketi / harici referans tam eşleşme; 4 haneli değer alıcı telefon son 4 (yalnız başka filtreyle).
Filtreler: `status` (çoklu), `hubCode` (origin/current/destination), `serviceLevel`, `delayed`, `createdFrom`/`createdTo` (ofsetli ISO-8601).
Sıralama allowlist: `-createdAt` (varsayılan), `createdAt`, `promisedDeliveryDate`, `-promisedDeliveryDate`, `statusChangedAt`, `-statusChangedAt`; tie-breaker `id`.
Keyset cursor: Data Protection ile korunan (kurcalanamaz) sürümlü yük; konum değerleri tick hassasiyetinde; filtre+sıralama+kullanıcıya bağlı. `limit` 1–200 (varsayılan 50). Toplam sayı yok.
Hub kapsamı SQL'de; liste öğesinde PII yok (yalnız alıcı şehri). `carrierCode` ve `hasOpenException` filtreleri ilgili tablolar gelince (dilim 5/6).
Kabul: yetki/kapsam doğru, sayfalama kapsam dışı veri/sayım sızdırmaz; aynı timestamp'te skip/duplicate yok; filtre kombinasyonları; boş/geçersiz cursor/limit;
alan görünürlüğü; Dilim 1 replay/detay regresyonu korunur; benchmark yok → hız iddiası yok.

### Dilim 3 sonucu — Verified

Kaynak: `files=123 sha256=60c6dea3e658ee7c466c3ee667fcdc89438c93a3023c3255b820c2e5435e1b5c`.
Değişenler: `src/Lst.Modules.Shipments/Application/ListShipments.cs`, `Endpoints/ShipmentEndpoints.cs` (GET /api/shipments), `ShipmentsModule.cs`,
`Infrastructure/ShipmentsModelContributor.cs` (2 indeks), migration `*_Slice3ListIndexes` (yalnız `ix_shipments_promised_id`, `ix_shipments_status_changed_id`; additive),
`tests/Lst.IntegrationTests/ShipmentListTests.cs` (+19), `tests/Lst.UnitTests/ShipmentListRequestTests.cs` (+8), docs/05 karar notu.
Contract: docs/05 §4.3 (cursor Data Protection, yarı açık tarih aralığı, sort allowlist, `invalid-cursor`/`cursor-mismatch`).

| Kontrol | Exit | Sonuç | Log |
|---|---|---|---|
| Release build | 0 | 0/0 | — |
| Pending model | 0 | yok | docs/verification/slice3.log |
| Unit | 0 | 72/72, skip 0 | docs/verification/slice3.log |
| Integration (gerçek PostgreSQL 18.6) | 0 | 48/48, skip 0 | docs/verification/slice3.log |

Doğrulananlar: aynı mikro saniyede 7 kayıt iki yönde skip/duplicate yok; tarih+id sıralaması; HubOperator kapsamı (sayfa başına 1 ile kapsam dışı satır yok,
kapsam dışı hub filtresi boş); filtre kombinasyonları; delayed (sahte saat); arama türleri; 12 geçersiz parametre; kurcalanmış/başka filtre/başka kullanıcı cursor'ı;
öğe alan allowlist'i (PII yok). Performans ölçülmedi.
---

## Dilim 4 — Hub tarama, durum makinesi, iptal

Kapsam: saf `ScanPolicy.Decide` (docs/02 geçiş matrisi + §3.4 paket kuralları), `POST /api/hubs/{hubCode}/scans` (Arrival/Departure/OutForDelivery,
`labelCode` + `clientScanId`), teknik ve iş tekrarı, konsolidasyon (paket konumu değişmeden önce), MissingDepartureScan notu, operatör iptali
(`POST /api/shipments/{id}/cancel`, `If-Match` zorunlu, çıkış sonrası red + `returnToSenderAllowed` ipucu), `allowedActions` durum+rol'den.
Tasarım kararları (ADR-014): her durum değiştiren komut gönderi satırını `SELECT … FOR UPDATE` ile kilitler (pessimistic aggregate lock; xmin tek başına
dokunulmayan satırı kilitlemez); teknik/iş tekrarı kontrolü ve olay INSERT aynı kilit altında; her timeline değişikliği gönderiyi "touch" eder (ETag değişir);
Rejected kararı paket/gönderi yazmaz (yalnız audit); iş anahtarı paket kimliğini içerir; paket-yalnız konum güncellemesi gönderi düzeyi stale kuralına
takılmaz (yalnız paket düzeyi). Retry yalnız otomatik akışlarda (taramada kilit beklemesi), operatör komutunda eski ETag → 412.
Kabul: izinli/izinsiz/terminal geçişler, bilinmeyen etiket 404, yetkisiz hub 403; ilk/sonraki paket, farklı hub split, kısmi yazım rollback;
teknik ID tekrarı / yeni ID aynı iş olayı tek etki; eşzamanlı taramalar, eski zamanlar, çakışan If-Match (deterministik barrier); tarama yanıtında PII yok;
`allowedActions` gerçek yetki/durumdan.

### Dilim 4 sonucu — Verified

Kaynak: `files=134 sha256=bd181b8b82133e2b8db1652b22b2533ec50df0157bce47828e1401f460a49b96`. ADR: `docs/adr/ADR-014-aggregate-lock-and-scan-decisions.md`.
Değişenler: `Domain/ScanPolicy.cs` (yeni), `Domain/Shipment.cs` (HasDeparted, ExtraAttemptsGranted, CancellationReason, paket konumu, ApplyScan, Cancel, olay fabrikaları),
`Application/{ShipmentCommandSupport,RecordHubScan,CancelShipment}.cs`, `GetShipmentDetail.cs` (paket konumu, `ShipmentActions`), `Endpoints/ShipmentCommandEndpoints.cs`,
Identity politikaları `OperationsStaff`, `HubScanner`; `AuditEntry.UserRejected`; migration `*_Slice4ScansAndCancel` (additive: 3 shipment + 3 package kolonu, 4 CHECK, FK, indeks).
Testler: `ScanPolicyTests` (+19), `HubScanTests` (+10), `CancelShipmentTests` (+6); fixture'a `hub.ank`, `hub.ist2`.
Contract: `POST /api/hubs/{hubCode}/scans` (200 Applied/Stale/Duplicate; 409 iş reddi kodları; 422 `invalid-occurred-at`/`hub-inactive`; 403; 404; 409 `client-scan-id-conflict`; 503 `shipment-busy`),
`POST /api/shipments/{id}/cancel` (If-Match; 200+ETag; 428; 412+güncel ETag; 409 `shipment-not-cancellable` + `currentStatus`, `returnToSenderAllowed`).
Detay: `packages[].currentHubCode/lastScanType/lastScannedAt`; `allowedActions` gerçek.
Bilinçli test güncellemesi: Dilim 1 `I11` artık Ops için `allowedActions == ["Cancel"]`, hub görevlisi için boş bekler (önceki boş beklenti, iptalin henüz olmamasından kaynaklıydı).

| Kontrol | Exit | Sonuç | Log |
|---|---|---|---|
| Release build | 0 | 0/0 | — |
| Pending model | 0 | yok | docs/verification/slice4.log |
| Unit | 0 | 91/91, skip 0 | docs/verification/slice4.log |
| Integration ×2 | 0 / 0 | 64/64 ve 64/64, skip 0 | docs/verification/slice4.log |
---

## Dilim 5 — Teslimat denemesi, istisna, göndericiye iade

Kapsam: `POST /api/shipments/{id}/delivery-attempts` (Idempotency-Key, numaralı/benzersiz deneme, hep-ya-hiç teslim, neden doğrulama, MaxAttempts=3 + ek hak);
`POST /api/shipments/{id}/return-to-sender`, `POST /api/shipments/{id}/return-completed` (If-Match; aynı gönderi lifecycle'ı, Delivered yeniden açılmaz,
v2 müşteri iadesi yok); yeni `Lst.Modules.Operations` modülü: `ShipmentException` (Open/Assigned/Resolved, gönderi+tip başına tek açık kayıt, occurrence sayacı),
`GET /api/exceptions` (keyset, filtreler), `POST /api/exceptions/{id}/assign|resolve` (If-Match; ReattemptApproved / ReturnToSenderInitiated gönderi komutuyla
aynı transaction), terminal durumda otomatik çözüm, konsolidasyon reddinde PackageSplit.
Modül sınırı (ADR-015): Shipments → Operations referansı yok; Shipments `IShipmentEventSink` olaylarını aynı DbContext/transaction'da yayınlar; Operations
çözüm aksiyonları için Shipments'ın `IShipmentOperations` sözleşmesini çağırır; Shipments açık istisnaları `operations.v_open_shipment_exceptions` view'ından okur.
Kabul: duplicate/eşzamanlı deneme aynı numarayı iki kez üretmez; max reddi ve yetkili ek hak bir kez etkiler; istisna eşzamanlı atama 412; çözüm hatası kısmi commit bırakmaz;
başarısız teslim/iade lifecycle'ı ve yasadışı terminal geçişler; hub/rol ve PII kuralları; gerçek allowedActions.

### Dilim 5 sonucu — Verified

Kaynak: `files=153 sha256=65e53c9e84ad26b534646a122d4336bdafba4a81a93f8fe882b56a99e1cbe77f`. ADR: `docs/adr/ADR-015-operations-module-boundary.md`.
Değişenler: yeni proje `src/Lst.Modules.Operations` (Domain/ShipmentException, Infrastructure, Application/{ExceptionEventSink,ExceptionQueue,ExceptionCommands}, OperationsModule);
Shipments: `Domain/DeliveryAttempt.cs` (entity, policy, domain olayları), `Shipment.cs` (RecordDeliveryAttempt, GrantExtraAttempt, StartReturnToSender, CompleteReturn, DeliveredAt),
`Application/{ShipmentContracts,OperatorCommands,RecordDeliveryAttempt}.cs`, `Endpoints/{IdempotencyHeader,ShipmentCommandEndpoints}.cs`, detay (openExceptions, attempts, allowedActions),
liste (`openExceptionCount`, `hasOpenException`); Platform: `Idempotency/IdempotentCommand.cs`, `AuditEntry.SystemSucceeded`; Dockerfile (yeni proje); migration
`*_Slice5AttemptsExceptionsReturns` (additive: `delivered_at`, `delivery_attempts`, `shipment_exceptions`, view `operations.v_open_shipment_exceptions`).
Testler: `DeliveryAndExceptionPolicyTests` (+12), mimari sınır testi (+1), `DeliveryAndExceptionTests` (+10). Fixture: `ops.queue`.
Bilinçli test güncellemeleri: `CancelShipmentTests` InTransit için `["ReturnToSender"]`, `HubScanTests` OutForDelivery için `["RecordDeliveryAttempt"]` (yeni eylemler).
Düzeltilen hata: istisna öğesinde `ETag` alanı JSON'da `eTag` çıkıyordu → `etag`.
Contract: `POST /api/shipments/{id}/delivery-attempts` (Idempotency-Key, 201 + Location + ETag, byte replay; 404/403/409/422),
`POST /api/shipments/{id}/return-to-sender` (If-Match, reason), `POST /api/shipments/{id}/return-completed` (If-Match),
`GET /api/exceptions` (status/type/hubCode/assignedTo=me|unassigned|id/overdue/sort=dueAt|-openedAt/limit/cursor), `POST /api/exceptions/{id}/assign|resolve` (If-Match; 403/409/412/422/428).

| Kontrol | Exit | Sonuç | Log |
|---|---|---|---|
| Release build + kilitli restore | 0 | 0/0 | — |
| Pending model | 0 | yok | docs/verification/slice5.log |
| Unit | 0 | 104/104, skip 0 | docs/verification/slice5.log |
| Integration ×2 | 0 / 0 | 74/74 ve 74/74, skip 0 | docs/verification/slice5.log |
| Docker image build | 0 | `lst-api:local` | docs/verification/slice5.log |
---

## Dilim 6 — Carrier webhook, inbox, simülatör

Kapsam: yeni `Lst.Modules.Tracking` (carriers, `carrier_event_receipts` inbox, `carrier_event_conflicts`, webhook, worker, simülatör);
`POST /api/carrier-webhooks/{carrierCode}` (ham byte HMAC-SHA256 `t.{raw}`, ±300 sn, sabit zamanlı karşılaştırma, çoklu secret, route/gövde carrier bağı,
64 KB, derinlik 16, eventType allowlist, carrier başına rate limit); kalıcı makbuz sonra `202 Received` (işlenmeden Applied dönmez);
teknik tekrar `200 Duplicate`, aynı ID farklı içerik `409` + kanıt + istisna; `CarrierInboxProcessor` (lease + fencing token, backoff, 5 iş hatasında
DeadLettered; geçici DB hatası deneme bütçesi tüketmez); kararlar Shipments'ta saf `CarrierEventPolicy` (Applied/Duplicate/Stale/Rejected/PendingReview);
gönderiye carrier atama (`POST /api/shipments/{id}/carrier`, If-Match); devir sonrası iptal yok; simülatör yalnız Development + `Simulation:Enabled`
(Production'da bayrak açıksa başlangıç reddi), olayları gerçek HTTP pipeline'ından imzalı gönderir.
Kabul: geçersiz/süresi geçmiş imza mutasyon yok; yanlış carrier/gönderi imzalı olay reddi; Duplicate×N / yeni ID iş tekrarı / paralel çakışma;
teslim sonrası sıra dışı olay geri sarmaz; sequence ve gerçekleşme kuralları; commit öncesi çökme rollback, sonrası dedupe; lease restart recover;
süresi dolan lease'i devralan işçi varken eski işçi commit edemez (fencing); DB kesintisi DLQ üretmez; ack kalıcı kayıttan sonra.

### Dilim 6 sonucu — Verified

Kaynak: `files=170 sha256=61a2684546d825b7970d88e98e79c7e5aa93f4ee8951d04f880e2730086df1bc`. ADR: `docs/adr/ADR-016-carrier-inbox-lease-and-fencing.md`.
Değişenler: yeni proje `src/Lst.Modules.Tracking` (Domain/CarrierEntities, Infrastructure/TrackingModelContributor (SIMEX referans verisi `HasData`),
Application/{CarrierWebhook (imza, sıkı JSON, normalize, kanonik hash), ReceiveCarrierEvent (`CarrierReceiptWriter`), CarrierInboxProcessor, CarrierSimulator},
Endpoints/TrackingEndpoints, TrackingModule (options doğrulama, rate limiter, worker, simülatör kaydı));
Shipments: `CarrierEventPolicy`, `Shipment.ApplyCarrierEvent/AssignCarrier`, `ShipmentCarrierEvents` (başka carrier'a ait gönderi makbuza bağlanmaz),
detay `carrier`, liste `carrierCode`; Identity: `Policies.OpsManagerOnly`; Api: `AddTrackingModule`, global rate limiter (`429 rate-limited` + `Retry-After`);
`db/runtime-role-grants.sql` (tracking: carriers SELECT, makbuz/çakışma SELECT/INSERT/UPDATE, DELETE yok); Dockerfile, compose (`simex_webhook_secret`,
`Simulation__Enabled` opt-in), `local-stack.ps1` (`-Simulation`, SIMEX secret üretimi); migration `*_Slice6CarrierInbox`
(additive: shipments carrier kolonları, tracking şeması, 3 tablo, CHECK'ler, FK'ler, `tracking_events.carrier_event_receipt_id` FK).
Testler: `CarrierWebhookAndPolicyTests` (+45), mimari sınır (+1), `CarrierWebhookTests` (+17).
Düzeltilen hatalar: carrier zamanı +03:00 ofsetiyle domain'e geçiyordu (Npgsql yalnız UTC yazar) → UTC; simülatör gövdesi PascalCase serileşiyordu → webhook JSON seçenekleri;
EF `migrations remove` DB bağlantısı istediği için Slice6 migration'ı model snapshot'ı Slice5 Designer'dan geri yüklenerek yeniden üretildi (pending model: yok).
Contract: `POST /api/carrier-webhooks/{carrierCode}` (202/200/409/400/401/413/415/429/503), `POST /api/shipments/{id}/carrier` (If-Match; 422 `unknown-carrier`),
`GET /api/shipments/{id}/carrier-receipts`, `GET /api/carrier-receipts?status=DeadLettered|Conflicted|PendingReview` (OperationsStaff),
`POST /api/dev/carrier-simulator/runs` (yalnız Development + bayrak, OpsManager).

| Kabul | Test |
|---|---|
| Ack yalnız kalıcı kayıttan sonra; işlenmemiş olay Applied görünmez | `Signed_event_is_acked_only_after_persistence_and_applied_asynchronously` |
| Teknik tekrar (aynı/biçimce farklı byte), paralel 8× tekrar tek makbuz | `Same_event_id_and_content_…`, `Parallel_redelivery_…` |
| Aynı ID farklı payload: orijinal değişmez, kanıt + istisna + audit + operatör listesi | `Same_event_id_with_different_content_…` |
| İmza/zaman/bilinmeyen carrier/gövde carrier bağı → 401, hiçbir şey yazılmaz | `Signature_timestamp_and_provider_binding_failures_…` |
| 64 KB, içerik tipi, derinlik, yinelenen alan, şema (değer yankılanmaz) | `Size_type_depth_duplicate_field_and_schema_limits_…` |
| Carrier başına rate limit 429 + Retry-After, bölüm ayrımı | `Per_carrier_rate_limit_returns_429_with_retry_after` |
| Applied/Duplicate(iş)/Stale(sıra dışı)/PendingReview(terminal çelişkisi)/Rejected(atanmamış carrier, bilinmeyen gönderi) | `Business_outcomes_…` |
| Eşzamanlı claim tek kazanan; lease devri sonrası eski işçi commit edemez (fencing) | `Expired_lease_owner_cannot_commit_after_takeover` |
| Commit öncesi hata: kısmi durum yok, sonra tek kez uygulanır | `Failure_after_apply_but_before_commit_…` |
| Poison: backoff, MaxAttempts sonrası DeadLettered + audit + operatör görünürlüğü | `Poison_event_backs_off_then_dead_letters_…` |
| Gerçek Npgsql bağlantı hatası: deneme bütçesi tüketilmez, DLQ yok, iyileşince uygulanır | `Database_outage_neither_consumes_attempts_…` |
| Depolama erişilemez → 503, ack yok | `Webhook_returns_503_without_ack_when_storage_is_unreachable` |
| Simülatör route'u yalnız Development + bayrak; Production/Testing'de bayrak açıksa başlangıç reddi; OpsAgent 403; gerçek pipeline + imza | `Simulator_…` (2 test) |
| Rol/If-Match/kapsam, runtime rol DELETE/carrier değişikliği reddi | `Carrier_assignment_and_receipt_endpoints_…`, `Runtime_role_cannot_delete_receipts_…` |

| Kontrol | Exit | Sonuç | Log |
|---|---|---|---|
| Kilitli restore + Release build | 0 / 0 | 0 uyarı / 0 hata | docs/verification/slice6.log |
| Pending model | 0 | yok | docs/verification/slice6.log |
| Unit | 0 | 150/150, skip 0 | docs/verification/slice6.log |
| Integration ×2 | 0 / 0 | 91/91 ve 91/91, skip 0 | docs/verification/slice6.log |
| Docker image build | 0 | `lst-api:local` | docs/verification/slice6.log |

Sınır: simülatörün gerçek HTTPS loopback gönderimi (Compose içinde) final duman testinde doğrulanacak; testlerde aynı pipeline TestServer üzerinden çağrılır.

---

## Dilim 7 — Outbox, SLA, KPI

Kapsam: PII'siz transactional outbox; claim/lease/bounded retry/dead-letter ve yetkili + audit'li replay (OpsManager); handler yoksa işlenmiş sayılmaz;
TimeProvider + Europe/Istanbul + tatillerle gecikme/hareketsizlik tespiti, idempotent istisna aç/güncelle/kapat; gerçek DB toplamlı KPI.
Kabul (docs/08): durum + outbox aynı transaction (rollback'te ikisi de yok); handler sonrası/`processed_at` öncesi çökmede yeniden işleme ve tüketici
tekrarı ayıklar; poison 8 denemede dead-letter; iki eşzamanlı SLA izleyici tek istisna; KPI'lar sabit veride beklenen sayılar.

### Dilim 7 sonucu — Verified

Kaynak: `files=184 sha256=869d7fcc3bcf443678577ae88faf58ca32c8fc4609c1ccce8bf63081f86b42f7`. ADR: `docs/adr/ADR-017-transactional-outbox-sla-and-kpi.md`.
Değişenler: Platform `Outbox/{OutboxMessage (entity, NotificationDelivery), OutboxDispatcher, OutboxServices (tipler, handler'lar, OutboxAdministration,
OutboxWorker, AddLstOutbox)}`, `Persistence/TransientFailures` (inbox ile ortak); Shipments `Infrastructure/ShipmentOutboxInterceptor`;
Operations `Infrastructure/ExceptionOutboxInterceptor`, `Application/{SlaMonitor (SlaCalendar, worker), OperationsSummary (KPI + audit)}`,
`ShipmentException.Escalate`, endpoint'ler; Api: interceptor kaydı (`AddInterceptors`), `AddLstOutbox`; grants (`outbox_messages` SELECT/INSERT/UPDATE/DELETE,
`notification_deliveries` SELECT/INSERT); migration `*_Slice7OutboxSla` (additive: `platform.outbox_messages`, `platform.notification_deliveries`).
Testler: `OutboxAndSlaTests` (+11; payload PII mimari testi dahil), `OutboxSlaSummaryTests` (+10), ortak `Infrastructure/CarrierHelper`.
Contract: `GET /api/operations/summary` (400/401/403/422), `GET /api/shipments/{id}/audit` (OpsManager; `before`, `limit`),
`GET /api/operations/outbox?status=`, `POST /api/operations/outbox/{messageId}/replay` (OpsManager + CSRF; 202/404/409).

| Kabul | Test |
|---|---|
| Durum değişikliği → PII'siz outbox, aynı transaction | `Status_changes_write_pii_free_outbox_messages_in_the_same_transaction` |
| Outbox INSERT hatası → durum, timeline, audit geri alınır | `Failed_outbox_insert_rolls_back_the_status_change_and_timeline_entry` |
| Handler sonrası çökme → yeniden teslim, tüketici tekrarı ayıklar, eski işçi yazamaz | `Crash_after_handler_before_processed_is_redelivered_…` |
| Handler yok → asla Processed değil, sonunda dead-letter | `Message_without_handler_is_never_marked_processed` |
| Poison 8 denemede dead-letter + audit; backoff; yalnız OpsManager replay + audit; 409/404 | `Poison_message_dead_letters_after_eight_attempts_…` |
| SLA aç/yükselt/kapat, idempotent, outbox + audit | `Sla_monitor_opens_escalates_and_resolves_idempotently_…` |
| İki eşzamanlı izleyici tek istisna | `Two_concurrent_sla_monitors_open_exactly_one_exception_per_type` |
| KPI'lar izole hub'da gerçek toplamlar (due, gecikmiş, zamanında, ilk deneme, istisna, neden, carrier) | `Summary_kpis_match_real_aggregates_for_an_isolated_hub` |
| Özet/audit/outbox rol ve doğrulama; audit'te PII yok; sayfalama | `Summary_and_audit_enforce_roles_validation_and_never_return_pii` |
| İş saati takvimi (Cumartesi iş günü, Pazar/tatil değil), backoff, önem yükseltme | `OutboxAndSlaTests` |

| Kontrol | Exit | Sonuç | Log |
|---|---|---|---|
| Kilitli restore + Release build | 0 / 0 | 0 uyarı / 0 hata | docs/verification/slice7.log |
| Pending model | 0 | yok | docs/verification/slice7.log |
| Unit | 0 | 161/161, skip 0 | docs/verification/slice7.log |
| Integration ×2 | 0 / 0 | 101/101 ve 101/101, skip 0 | docs/verification/slice7.log |
| Docker image build | 0 | `lst-api:local` | docs/verification/slice7.log |

---

## Dilim 8 — Public tracking, rate limit, retention

Kapsam: PublicTrackingCode + allowlist DTO; geçersiz/bulunamayan aynı 404; no-store/no-referrer/noindex; güvenilir proxy politikalı rate limit;
OpsManager kod yenileme; demo saklama (PII maskeleme, ham payload temizliği, outbox temizliği); receipt silme ↔ FK çelişkisinin çözümü (tombstone);
tekrar ayıklama saklama ufkunun hizalanması; public kodlu URL'lerin log'dan arındırılması.
Kabul (docs/08): alan kümesi sabit; seed PII yanıtta yok; aynı 404; 21. istek 429; yenilemeden sonra eski kod 404; saklama 180 günü geçmiş terminal
gönderileri maskeler, diğerlerine dokunmaz, iki kez çalışınca değişiklik yapmaz.

### Dilim 8 sonucu — Verified

Kaynak: `files=196 sha256=dedba6155f9bdb9293a2c75d20240be83d6730e9fdd8939e6f713449713552b3`. ADR: `docs/adr/ADR-018-public-tracking-rate-limit-retention.md`.
Değişenler: Shipments `Application/PublicTracking.cs` (görünüm, kod biçimi, maskeleme, `RotatePublicTrackingCodeHandler`), `Endpoints/PublicTrackingEndpoints.cs`,
`Infrastructure/ShipmentPiiRetentionTask.cs`, `Shipment.RotatePublicTrackingCode`/`PiiRedactedAt`, party PII alanları nullable, detay `publicTrackingCode`/`piiRedacted`;
Platform `Http/RateLimiting.cs` (limiter, güvenilir proxy, public başlıklar), `Retention/RetentionJob.cs` (iş, worker, outbox/idempotency görevleri);
Operations `ExceptionNoteRetentionTask` + `POST /api/operations/retention/run`; Tracking `RawPayloadRetentionTask`; Api: log filtreleri, `UseLstForwardedHeaders`,
`UsePublicResponseHeaders`, `AddLstRateLimiting`, `AddLstRetention`; migration `*_Slice8PublicTrackingRetention`
(party kolonları nullable + CHECK, `pii_redacted_at`, aday indeksi).
Testler: `PublicTrackingAndRetentionTests` (+8), `PublicTrackingRetentionTests` (+6).
Bulunan ve düzeltilen hatalar: (1) varsayılan loopback güveni temizlenince ForwardedHeaders tüm kaynaklara güveniyordu → liste boşsa middleware eklenmiyor;
(2) iptal nedeni NULL'lanınca `ck_shipments_cancelled_requires_reason` ihlali → `[redacted]`.

| Kabul | Test |
|---|---|
| Alan kümesi sabit, yalnız Applied olaylar, PII/hub kodu/harici referans/facility yok, public başlıklar, kod log'da yok | `Public_view_returns_only_allowlisted_fields_and_applied_events`, `Public_view_field_set_is_fixed` |
| Geçersiz ve bulunamayan kod: byte düzeyinde aynı 404 + başlıklar | `Invalid_and_unknown_codes_get_byte_identical_404s_with_public_headers` |
| 21. istek 429 + Retry-After; sahte X-Forwarded-For yok sayılır; iç API etkilenmez | `Twenty_first_request_per_ip_is_429_and_spoofed_forwarded_for_is_ignored` |
| X-Forwarded-For yalnız güvenilir proxy'den | `Forwarded_for_is_honored_only_from_a_configured_trusted_proxy` |
| Kod yenileme: yalnız OpsManager, If-Match, eski kod 404, audit kod içermez, hub'a kod gösterilmez | `Ops_manager_rotates_the_code_old_code_stops_working_and_audit_has_no_code` |
| Saklama: eski terminal maskelenir, terminal olmayan ve yeni olana dokunulmaz, ham payload temizlenir, tombstone + FK kalır, idempotent, audit | `Retention_masks_old_terminal_pii_purges_raw_payloads_keeps_tombstones_and_is_idempotent` |

| Kontrol | Exit | Sonuç | Log |
|---|---|---|---|
| Kilitli restore + Release build | 0 / 0 | 0 uyarı / 0 hata | docs/verification/slice8.log |
| Pending model | 0 | yok | docs/verification/slice8.log |
| Unit | 0 | 169/169, skip 0 | docs/verification/slice8.log |
| Integration ×2 | 0 / 0 | 107/107 ve 107/107, skip 0 | docs/verification/slice8.log |
| Docker image build | 0 | `lst-api:local` | docs/verification/slice8.log |

---

## Dilim 9 — React operasyon paneli ve mobil public UI

Kapsam (prompt): React + TypeScript, aynı origin SPA (`src/web`); dev proxy cookie/CSRF taşır; üretim statik dosyaları mevcut ASP.NET host içinden.
Ekranlar: giriş, gönderi listesi/filtre, gönderi oluşturma, detay + zaman çizelgesi, izinli işlemler (iptal, iade, iade tamamlama, teslimat denemesi,
carrier atama, public kod yenileme), hub tarama, istisna kuyruğu (üzerine al / çöz), operasyon özeti (gerçek KPI), mobil public takip (`/t/{kod}`).
Gerçek API; başarı yolu mock/fixture değil. Yükleniyor/boş/hata/403/404/bayat veri, doğrulama, 412 çakışmada yetkili yenileme, yanıt kaybında aynı
Idempotency-Key ile tekrar (alan değişince yeni anahtar politikası). Kimlik bilgisi/CSRF/cookie/public kod console/log'a yazılmaz; `/t/*` referrer koruması.
Operasyon estetiği: yoğun tablo, durum = ikon + metin, ölçülü renk (design/operations-preview.html token'ları); gradient/glow/cam/AI markası yok.
Kabul: typecheck + lint + production build; anlamlı bileşen testleri; gerçek tarayıcı + ayrı DB ile E2E (login/CSRF, create + yanıt kaybı replay,
filtre/detay, izinli tarama/iptal, istisna işlemi, public redaksiyon); masaüstü/tablet/mobil, klavye odağı/etiket/kontrast (axe), boş/hata/yetki durumları;
ETag çakışmasında yetkili yenileme; gizlenen buton yanında API reddi testi; sahte KPI ve çalışmayan başarı butonu yok; CI frontend build/test + izole E2E
artifact'ları (auth izleri secret içermez).

### Dilim 9 sonucu — Verified (remote CI çalıştırılmadı)

Kaynak (ilerleme dosyası yazılmadan hemen önce): `files=257 sha256=dbb6c2c9a7c35dbba125b773d8364321a403ad0151ead7877967a41d3d5a2c9d`. ADR: `docs/adr/ADR-019-spa-hosting-ui-stack-and-e2e.md`.
Değişenler: yeni `src/web` (React 19 + TypeScript 6 + Vite 8; `src/api` istemci/CSRF, `src/auth`, `src/components`, `src/features/{login,shipments,scan,exceptions,summary,public}`,
`src/lib`, `src/styles/app.css`, bileşen testleri, `e2e/*.spec.ts`, `playwright.config.ts`); Api: `Hosting/SpaHosting.cs`, `Program.cs` (statik dosya + kabuk fallback);
Operations: `GET /api/reference/hubs`, istisna kuyruğu `shipmentId` filtresi; Tracking: `GET /api/reference/carriers`, `GET /api/dev/carrier-simulator`;
`Dockerfile` (Node build aşaması → `wwwroot`), `.gitignore`, `.github/workflows/ci.yml` (`web`, `e2e` işleri), `scripts/ci-e2e-stack.sh`;
testler `SpaHostingTests` (+14), `ReferenceAndQueueFilterTests` (+4); docs/05, docs/07 karar notları. Migration: yok.
Contract: docs/05 "Karar değişikliği (Dilim 9)".

| Kabul | Kanıt |
|---|---|
| Typecheck / lint / production build | `npm run typecheck`, `npm run lint` (0 uyarı), `npm run build` — exit 0 |
| Anlamlı bileşen testleri | `npm run test` → 7 dosya, 38/38: idempotency anahtar politikası, CSRF yenileme (tek tekrar), 412 → yenile + metni koru, `allowedActions`'tan buton türetme, tarama sonucu/ret/yanıt kaybı, istisna çözüm formu, public allowlist + kodun başlık/depolama/console'a yazılmaması |
| Login/CSRF (gerçek tarayıcı) | `01-auth`: yönlendirme, genel hata, `__Host-` HttpOnly/Secure/SameSite=Strict cookie, token'sız POST 400, oturum bitince giriş ekranı + neden |
| Create + yanıt kaybı replay | `02-create-replay`: ilk 201 sunucuda işlenip yanıt koparılır → aynı Idempotency-Key ile tekrar → `Idempotency-Replayed`, sunucuda tek gönderi |
| Filtre / detay | `03-list-detail`: URL'de filtre, kombinasyon, boş sonuç, referans araması, tracking no → detay, `/` kısayolu, aria-sort, KPI = API sayıları |
| İzinli tarama / iptal | `04-scan-cancel`: uygulandı / tekrar / ret (neden + koli sayısı), bilinmeyen etiket; ops iptali; çıkış sonrası iptal yok + açıklama |
| ETag çakışması | `04-scan-cancel`: gerçek eşzamanlı tarama → 412 → güncel durum yüklenir, neden korunur, ikinci onayla iptal; API'de eski ETag 412, güncel ETag 409 |
| Gizlenen buton yanında API reddi | Hub görevlisi geçerli CSRF + If-Match ile iptal → 403; ops kullanıcısı tarama → 403; hub.izm `/api/exceptions`, `/api/operations/summary` → 403; kapsam dışı detay 404 |
| İstisna işlemi | `05-exceptions`: başarısız teslimat (UI) → istisna → üzerine al → iade çözümü → kuyruktan düşer, gönderi `ReturningToSender`; eşzamanlı atamada 412 + kuyruk güncellenir |
| Public redaksiyon (mobil 360 px) | `06-public`: yanıt anahtar kümesi sabit; HTML ve yanıtta ad/telefon/adres/ilçe/posta kodu/referans/tam tracking no/hub kodu/neden kodu yok; Referer ve cookie gönderilmez; iç API çağrılmaz; yenilenen kod sonrası eski bağlantı çalışmaz ve eski veri gösterilmez; 44 px dokunma hedefi |
| Erişilebilirlik / klavye | `07-accessibility-states`: axe (WCAG 2.x A/AA) giriş, liste, oluşturma + hatalar, detay + onay paneli, istisna + çözüm paneli, özet (açık ve koyu), tarama, public → ihlal 0; yalnız klavyeyle liste → detay → iade |
| Boş / hata / yetki / bayat | Filtre kaynaklı boş durum, ilk kullanım boş durumu (boş DB), ağ hatası + yeniden dene, 403 ekranı, 404 ekranı, "Canlı güncelleme kapalı · son yenileme" |
| Masaüstü / tablet / mobil | `08-screens`: 12 ekran × 3 genişlik (1440 / 820 / 390), belge yatay taşması yok; görüntüler gözle incelendi (açık + koyu) |
| Sahte KPI / çalışmayan buton yok | KPI'lar `GET /api/operations/summary`'den; E2E API değerleriyle karşılaştırır; sekme sayaçları uydurulmaz |
| Dev proxy | Vite `http://localhost:5173` → API: `01-auth`, `02-create-replay`, `04-scan-cancel` 8/8 |
| CI | `web` + `e2e` işleri eklendi; action'lar SHA ile sabit (setup-node v7.0.0 doğrulanmış commit). **Remote koşu yok.** Yerel eşdeğer: `scripts/ci-e2e-stack.sh` Git Bash'te çalıştırıldı, temiz DB'de E2E 21/21 |

| Kontrol | Komut | Exit | Sonuç | Log |
|---|---|---|---|---|
| Kilitli restore + Release build | `dotnet restore Lst.slnx --locked-mode`; `dotnet build Lst.slnx -c Release --no-restore` | 0 / 0 | 0 uyarı / 0 hata | docs/verification/slice9-backend.log |
| Pending model | `dotnet ef migrations has-pending-model-changes …` | 0 | yok | aynı |
| Unit | `dotnet test --project tests/Lst.UnitTests/…` | 0 | 169/169, skip 0 | aynı |
| Integration ×2 | `dotnet test --project tests/Lst.IntegrationTests/…` | 0 / 0 | 125/125 ve 125/125, skip 0 | aynı |
| Web statik kontroller | `npm run typecheck`, `npm run lint`, `npm run build` (src/web) | 0 / 0 / 0 | temiz | — |
| Bileşen testleri | `npm run test` | 0 | 38/38 | — |
| Image + izole yığın | `bash scripts/ci-e2e-stack.sh .local-ci lst-ci 18444` (ve `local-stack.ps1 up -Project lst-e2e …`) | 0 | postgres/api healthy, migrator exit 0 | docs/verification/slice9-stack-up.log |
| E2E (boş DB) | `npm run e2e` (`LST_E2E_CHANNEL=chrome`) | 0 | 21/21, fail 0, skip 0; ikinci koşu 21/21 | docs/verification/slice9-e2e.log |

E2E'nin bulduğu ve düzeltilen gerçek kusurlar: (1) public sayfada oturum sağlayıcısı `/api/auth/me` çağırıyor ve 401 işleyicisi public sorguyu iptal ediyordu →
sağlayıcı yalnız iç uygulamada, `/me` yoklaması işleyiciyi tetiklemez; (2) "oturum sona erdi" mesajı istek sırasına bağlıydı → sıra bağımsız; (3) mobilde tablo içindeki
görünmez metinler belgeyi yatay taşırıyordu; (4) filtre kontrolleri URL güncellemesi ertelendiği için bir an eski değere dönüyordu → eşzamanlı güncelleme;
(5) sarmalayan `<label>` yüzünden filtre adları seçenek metinleriyle kirleniyordu → açık `label/for`; (6) teslimat denemesi zamanı dakikaya yuvarlanınca dağıtıma çıkıştan
önceye düşebiliyordu; (7) kod yenilendikten sonra public sayfa eski veriyi gösteriyordu.

Görev kaynakları: Compose projesi `lst-e2e` (çalışıyor; `.local-e2e/`), `lst-ci` doğrulama yığını ve `.local-ci/` **silindi**. Web: `src/web/node_modules`, `dist`, `test-results`, `playwright-report` (git dışı).

---

## Dilim 10 — SignalR ve reconnect

Kapsam (prompt): aynı origin, kimlikli SignalR (`/realtime/operations`); sunucu yönetimli `SubscribeOperations/Hub/Shipment` yetkilendirmesi (istemci grup adı
belirleyemez); rol/hub ataması değişimi bağlantı ömründe yeniden doğrulama + açık iptal (cookie damgasına güvenilmez); outbox'tan PII'siz en küçük geçersizleme
mesajları (yetkisiz aboneye metadata da gitmez); API commit'i SignalR gönderimine bağlı değil, yinelenen mesaj zararsız; (yeniden) bağlanmada ve ilk abonelikte
yetkili HTTP yeniden eşitleme (cursor garantisi yok → aktif detay/liste/kuyruk/KPI sorguları tazelenir) + periyodik mutabakat; mesaj ipucu, DB otorite;
sıra/lookback sezgisi kayıpsız akış diye sunulmaz; arayüzde bağlı / kopuk / yeniden bağlanıyor / bayat durumları ve debounce'lu tazeleme.
Kabul: yetkili/yetkisiz kullanıcı ve hub abonelikleri, mevcut bağlantının iptali, public reddi; kopukken DB güncellemesi → yeniden bağlanınca yetkili durum;
geciken commit, yinelenen/sırasız bildirim, outbox replay, dead-letter/retry, worker yeniden başlatma; cookie süresi/çıkış/iptal; hassas yük yok;
tarayıcıda gerçek zamanlı ve yeniden bağlanma E2E (polling telafisi WebSocket başarısı diye sunulmaz).

### Dilim 10 sonucu — Verified (remote CI çalıştırılmadı)

Kaynak (ilerleme dosyası yazılmadan hemen önce): `files=267 sha256=bcd5df582e33eea5936e01ab3f3ac114c8a3f1ba578ea5c1934ee677560438e8`. ADR: `docs/adr/ADR-020-realtime-invalidation-and-resync.md`.
Değişenler: Api `Realtime/{OperationsRealtimeHub (hub, mesajlar, RealtimeAuthorizer), RealtimeConnections (kayıt), RealtimeServices (SignalR bildirici,
yeniden doğrulayıcı + worker, kayıt, Origin kontrolü)}`, `Program.cs`; Platform `Realtime/IRealtimeSessions`, `OutboxMessageTypes.ExceptionAssigned` + handler kaydı;
Operations `ExceptionOutboxInterceptor` (atama mesajı); Identity çıkışta bağlantı kapatma; Tracking: inbox claim sırası ve simülatör zamanlaması düzeltmeleri;
web `src/realtime/{RealtimeClient,RealtimeProvider}`, `Layout` durum şeridi, detayda gönderi aboneliği, `@microsoft/signalr` 10.0.11;
testler `RealtimeTests` (+7), `CarrierWebhookTests` (+1), `RealtimeClient.test.ts` (+7), `e2e/09-realtime.spec.ts` (+7);
test bağımlılığı `Microsoft.AspNetCore.SignalR.Client` 10.0.12 (kilit dosyası güncellendi). Migration: yok.
Contract: docs/05 "Karar değişikliği (Dilim 10)" — changes feed uygulanmadı (gerekçeli sapma).

| Kabul | Kanıt |
|---|---|
| Yetkili/yetkisiz kullanıcı ve hub abonelikleri; istemci grup adı veremez | `Subscriptions_are_authorized_by_current_role_hub_assignment_and_shipment_scope` (ops hub'a, hub görevlisi operations'a/başka hub'a/kapsam dışı gönderiye abone olamaz; kapsam dışı = bulunamadı) |
| Public/anonim ve yabancı origin reddi | `Anonymous_and_cross_origin_clients_cannot_connect` (negotiate 401; `Origin: https://evil.example` 403); E2E "oturumsuz istemci canlı hub'a bağlanamaz" |
| Mevcut bağlantının iptali (rol / hub ataması) | `Revalidation_revokes_existing_subscriptions_when_hub_assignment_or_role_is_removed`: atama silinince `SubscriptionRevoked` + sonraki mesaj gelmez; rolü kalmayan kullanıcının bağlantısı kapanır ve yeniden bağlanamaz |
| Çıkış / cookie süresi | `Logout_closes_the_users_connections_and_ticket_expiry_closes_stale_ones` (çıkış yalnız o kullanıcıyı keser; 8 saatlik bilet dolunca bağlantı kapanır); E2E çıkışta ve cookie geçersizleşince WebSocket kapanır, giriş ekranı |
| Hassas yük yok; yetkisiz aboneye metadata da gitmez | `Invalidations_come_from_the_outbox_are_pii_free_and_reach_only_authorized_groups` (alan kümesi sabit: `shipmentId`, `status`; başka hub'ın görevlisi sessiz); E2E WebSocket çerçevelerinde PII/referans/tracking no yok, hub.izm'e ilgisiz gönderi için hiç çerçeve gelmez |
| API commit'i yayına bağlı değil; retry / worker yeniden başlatma / yinelenen bildirim | `Publish_failure_never_affects_the_command_and_is_retried_then_replayed_harmlessly` (yayın bozukken komut başarılı; mesaj işlenmemiş kalır, backoff sonrası teslim; handler sonrası çökme → yeniden yayın, sunucu durumu değişmez) |
| Geciken commit (dağıtılmamış mesaj) | Aynı testlerde: dağıtıcı çalışmadan hiçbir istemci mesaj almaz |
| Outbox replay / dead-letter | Dilim 7 testleri (`Poison_message_dead_letters_after_eight_attempts_…`) aynı dağıtıcıyı kapsar; realtime handler'ı aynı yoldan geçer |
| İstisna ataması bildirimi | `Exception_changes_are_published_with_ids_only_including_assignment` |
| Kopukken DB güncellemesi → yeniden bağlanınca yetkili durum | E2E "kopukken yapılan değişiklik mesajla gelmez…": WebSocket koparılır + el sıkışma engellenir → şerit "yeniden bağlanılıyor / bayat olabilir"; DB değişir, ekran 3 sn eski durumu gösterir; bağlantı gelince HTTP eşitlemesiyle yeni durum; ardından itme yeniden çalışır |
| Tarayıcıda gerçek zamanlı (itme, yoklama değil) | E2E liste / istisna kuyruğu / hub kapsamı / simülatör: güncellemeler ≤ 8 sn'de (mutabakat aralığı 60 sn) ve `ShipmentChanged` çerçevesi yakalanarak doğrulanır |
| Uçtan uca hat (webhook → inbox → outbox → SignalR, gerçek worker'lar, Compose içi HTTPS loopback) | E2E simülatör `HappyPath`: detay yenilenmeden "Teslim edildi"; Dilim 6'dan ertelenen "Compose içinde simülatör loopback" kontrolü de böylece doğrulandı |
| İstemci mantığı | `RealtimeClient.test.ts`: role göre abonelik + ilk eşitleme, debounce/tekilleştirme, kopma → oturum yoklama → yeniden abonelik + eşitleme, geri çekilmeli yeniden deneme, reddedilen abonelik, periyodik mutabakat, durdurma |

| Kontrol | Komut | Exit | Sonuç | Log |
|---|---|---|---|---|
| Kilitli restore + Release build | `dotnet restore Lst.slnx --locked-mode`; `dotnet build Lst.slnx -c Release --no-restore` | 0 / 0 | 0 uyarı / 0 hata | docs/verification/slice10-backend.log |
| Pending model | `dotnet ef migrations has-pending-model-changes …` | 0 | yok | aynı |
| Unit | `dotnet test --project tests/Lst.UnitTests/…` | 0 | 169/169, skip 0 | aynı |
| Integration ×2 | `dotnet test --project tests/Lst.IntegrationTests/…` | 0 / 0 | 133/133 ve 133/133, skip 0 | aynı |
| Web | `npm run typecheck`, `npm run lint`, `npm run test`, `npm run build` | 0 | temiz; 45/45 | — |
| Image + yığın | `scripts/local-stack.ps1 up -Project lst-e2e -LocalDir .local-e2e -Port 18443 -SeedDemo -Simulation` | 0 | healthy | — |
| E2E (tam paket) | `npm run e2e` (`LST_E2E_CHANNEL=chrome`) | 0 | 28/28, fail 0, skip 0 | docs/verification/slice10-e2e.log |

Bu dilimde bulunan ve düzeltilen kusurlar: (1) Dilim 6 — inbox partisinde işleme sırası `RETURNING` sırasına bağlıydı (sıralı akışta `Stale`); (2) Dilim 6 — simülatör yeni
gönderide tüm olayları aynı saniyeye yerleştiriyordu; (3) Dilim 9 — "oturum sona erdi" algısı canlı kanalla birlikte kendiliğinden çalışır hale geldi (E2E güncellendi).

---

## Final doğrulama (F) — tamamlandı

Ayrıntı: [`docs/final-verification.md`](final-verification.md). Ham çıktı: `docs/verification/final.log`, `final-e2e.log`, `final-migration.log`, `final-stack-smoke.log`.

Doğrulanan kaynak: tam koşu `files=271 sha256=12f95652b368e39faad62f99514d872bd44d9899014917077b66d0f3c5cb9408` (başlangıç = bitiş); ardından tek kaynak değişikliği
(`src/web/src/styles/app.css`) için web kontrolleri + E2E yeniden koşuldu: `files=271 sha256=aa48425a6cbfb9e0e268cf8d6150257d0abd026b2abe2e0e05c0ede6a7a95622`.
Sonrasında yalnız belgeler değişti.

| Kontrol | Exit | Sonuç |
|---|---|---|
| Araç + kilitli restore, Release build | 0 | 0 uyarı / 0 hata |
| Unit | 0 | 169/169, skip 0 |
| Integration (PostgreSQL 18.6) | 0 | 133/133, skip 0 |
| Web: `npm ci`, typecheck, lint, build | 0 | temiz |
| Web bileşen testleri | 0 | 45/45 |
| Pending model + idempotent SQL | 0 | değişiklik yok; script üretildi |
| Migration kurulum + Dilim 1/5'ten verili yükseltme | 0 | 16/16 |
| Temiz izole yığın `lst-final` (HTTPS) | 0 | healthy; image UID 1654, secret/kaynak haritası yok |
| E2E (gerçek Chrome, boş DB) | 0 | 28/28, fail 0, skip 0 (CSS değişikliğinden sonra yeniden 28/28) |
| HTTPS duman testi | 0 | 20/20 |
| Log / secret / yazarlık / lisans taramaları | — | bulgu yok; `THIRD-PARTY-NOTICES.md` üretildi |
| Workflow YAML + bağımlılık zafiyet listesi | 0 | hata yok; bulgu yok |
| Uzak CI | — | **Çalıştırılmadı** |

Kalan iş: yok. Engel: yok. Çalışan container/volume/süreç: yok (bkz. final-verification §7).
Devam etmek isteyen için sonraki olası adım (bu görevin kapsamı dışında): docs/08 Dilim 11 (runbook, benchmark planı) ve uzak CI koşusu.
