# Final Doğrulama (Dilim 2–10)

> Tarih: 2026-10-10 · Proje sahibi: TCoderr · Ham çıktılar: [`verification/`](verification/) (redakte; secret, cookie, CSRF token, parola, PII yok)
> Bu belge yerel doğrulamadır. **Uzak CI çalıştırılmadı, deployment yapılmadı, yük testi yapılmadı.**

## 1. Sonuç

Dilim 2–10 uygulanmış ve **mevcut kaynak üzerinde yerelde doğrulanmıştır**. Açık engel yoktur. Doğrulanmamış tek madde uzak GitHub Actions
koşusudur (depo yok/erişim yetkisi yok); workflow hazırdır ve adımlarının yerel eşdeğerleri çalıştırılmıştır.

| Dilim | Durum | Mevcut kaynakta kanıt |
|---|---|---|
| 1 Oluşturma + idempotency + detay | Doğrulandı (regresyon) | unit + integration (final koşu) + duman testi create/replay |
| 2 Docker, yerel yığın, CI | Doğrulandı · uzak CI **koşulmadı** | image build, izole Compose healthy, HTTPS duman 20/20 |
| 3 Liste/arama/sayfalama | Doğrulandı | integration + E2E `03-list-detail` |
| 4 Tarama, durum makinesi, iptal | Doğrulandı | unit + integration + E2E `04-scan-cancel` |
| 5 Teslimat denemesi, istisna, iade | Doğrulandı | unit + integration + E2E `05-exceptions` |
| 6 Carrier webhook, inbox, simülatör | Doğrulandı (2 kusur bu oturumda düzeltildi) | integration (+1 regresyon) + E2E simülatör (Compose içi HTTPS loopback dahil) |
| 7 Outbox, SLA, KPI | Doğrulandı | unit + integration + E2E KPI = API |
| 8 Public takip, rate limit, retention | Doğrulandı | integration + E2E `06-public` |
| 9 React paneli + mobil public UI | Doğrulandı | typecheck, lint, 45 bileşen testi, E2E, axe, ekran görüntüleri |
| 10 SignalR ve reconnect | Doğrulandı | integration `RealtimeTests` + E2E `09-realtime` |

## 2. Doğrulanan kaynak

Git deposu yoktur; sonuçların ait olduğu kaynak `scripts/source-fingerprint.ps1` ile kaydedilir (build çıktıları, `node_modules`, `.local*`, `docs/verification` hariç).

| An | Parmak izi |
|---|---|
| Tam final koşusu (başlangıç = bitiş) | `files=271 sha256=12f95652b368e39faad62f99514d872bd44d9899014917077b66d0f3c5cb9408` |
| Final koşudan sonraki tek kaynak değişikliği: `src/web/src/styles/app.css` (dar ekranda üst çubuk yapışkan değil) — web kontrolleri ve E2E yeniden koşuldu | `files=271 sha256=aa48425a6cbfb9e0e268cf8d6150257d0abd026b2abe2e0e05c0ede6a7a95622` |
| Sonrasında yalnız belgeler değişti (bu dosya ve ilerleme kaydı) | — |

Ortam: .NET SDK 10.0.401 · Node 24.18.0 · Docker Engine 29.8.2 · PostgreSQL 18.6 · Chrome 155 (Playwright 1.64 `chrome` kanalı) · Windows 11.

## 3. Bu oturumda gerçekten çalıştırılan kontroller

Tümü [`verification/final.log`](verification/final.log) içindedir (komut başına exit kodu).

| # | Kontrol | Komut | Exit | Sonuç |
|---|---|---|---|---|
| F1 | Araç + kilitli restore | `dotnet tool restore`; `dotnet restore Lst.slnx --locked-mode` | 0 / 0 | kilit hatası 0 |
| F1 | Release build | `dotnet build Lst.slnx -c Release --no-restore` | 0 | 0 uyarı / 0 hata (uyarılar hata sayılır) |
| F1 | Web kurulum (kilit) | `npm ci` (src/web) | 0 | 259 paket |
| F1 | Web typecheck / lint / build | `npm run typecheck`, `npm run lint`, `npm run build` | 0 / 0 / 0 | temiz; kaynak haritası üretilmez |
| F2 | Unit | `dotnet test --project tests/Lst.UnitTests/…` | 0 | **169 / 169**, fail 0, skip 0 |
| F2 | Integration (gerçek PostgreSQL 18.6, Testcontainers) | `dotnet test --project tests/Lst.IntegrationTests/…` | 0 | **133 / 133**, fail 0, skip 0 |
| F2 | Web bileşen testleri | `npm run test` | 0 | **45 / 45** (8 dosya) |
| F2 | E2E (gerçek Chrome, izole yığın, **boş DB**) | `npm run e2e` | 0 | **28 / 28**, fail 0, skip 0 — [`final-e2e.log`](verification/final-e2e.log) |
| F3 | Model ↔ migration | `dotnet ef migrations has-pending-model-changes …` | 0 | bekleyen değişiklik yok |
| F3 | Idempotent SQL | `dotnet ef migrations script --idempotent … -o artifacts/migrations/migrate.sql` | 0 | üretildi (54 KB) |
| F3 | Boş DB kurulumu + önceki dilimlerden yükseltme | `python scripts/migration_upgrade_check.py` | 0 | **16 / 16** — [`final-migration.log`](verification/final-migration.log) |
| F4 | Image + izole Compose (HTTPS) | `scripts/local-stack.ps1 init/up -Project lst-final -LocalDir .local-final -Port 18445 -SeedDemo -Simulation` | 0 | postgres/api healthy, migrator exit 0 |
| F4 | HTTPS duman testi | `python scripts/stack_smoke.py --project lst-final --local-dir .local-final --port 18445` | 0 | **20 / 20** — [`final-stack-smoke.log`](verification/final-stack-smoke.log) |
| F4/F7 | Image denetimi | `docker image inspect`, `docker history`, `exec id -u` | — | kullanıcı UID 1654 (root değil); env ve katman geçmişinde secret benzeri değer 0; `wwwroot` yalnız `index.html`, `assets`, `third-party-notices.txt` |
| F5 | Log taraması (çalışan yığın) | API logunda sentetik PII, public kodlu yol, `.local-final` altındaki her secret/parola | — | eşleşme 0; 4 `Error` satırının tamamı duman testinin kasıtlı DB kesintisine ait |
| F7 | Yazarlık / imza / secret taraması | `grep` (kaynak + belgeler + loglar) | — | `Authors=TCoderr`; AI/araç imzası yok (yalnız `AGENTS.md / CLAUDE.md` dosya adı atıfları); gizli değer kalıbı yok |
| F7 | Üçüncü taraf lisansları | `python scripts/third_party_notices.py` | 0 | 26 npm + 27 NuGet paketi; `THIRD-PARTY-NOTICES.md` + arayüzle dağıtılan tam metinler |
| F7 | Bağımlılık zafiyet listesi | `npm audit --omit=dev`; `dotnet list Lst.slnx package --vulnerable --include-transitive` | 0 / 0 | bulgu yok |
| F2/CI | Workflow sözdizimi | `yaml` ayrıştırıcısıyla `ci.yml` | 0 | hata 0; 3 iş (`backend`, `web`, `e2e`); tüm action'lar SHA ile sabit. **Uzak koşu yok** |

Kesinti sonrası ilk iş olarak koşulan regresyon (`verification/resume-baseline.log`) ve dilim gate'leri (`slice9-*.log`, `slice10-*.log`) ilerleme kaydındadır.

### Migration doğrulaması (F3) ayrıntısı
Tek kullanımlık, betiğin kendi oluşturup sildiği bir PostgreSQL 18.6 konteynerinde: (1) boş DB'ye idempotent script → 7 migration; ikinci uygulama şemayı
değiştirmez; (2) **Dilim 1** ve **Dilim 5** şemasında örnek veri (2 hub, gönderi, 2 taraf, koli, olay) içeren DB'ler son sürüme yükseltilir → veri korunur,
yeni NOT NULL kolonlar varsayılanla dolar, referans veri (SIMEX) eklenir; (3) yükseltilen şema, yeni kurulum şemasıyla aynıdır (`pg_dump --schema-only`,
boşluktan bağımsız karşılaştırma); (4) yeniden uygulama değişiklik yapmaz. Tüm migration'lar additive'dir; veri kaybettiren migration yoktur.

### Kritik senaryolar (F5) — hangi test neyi kanıtlıyor

| Alan | Kanıt |
|---|---|
| Kimlik / CSRF | E2E `01-auth` (yönlendirme, genel hata, `__Host-` HttpOnly/Secure/SameSite=Strict, token'sız POST 400, oturum bitişi); integration `AuthAndCsrfTests` |
| Rol / hub matrisi | E2E: hub görevlisi iptal 403, ops tarama 403, hub.izm istisna/özet 403, kapsam dışı detay 404; integration `ShipmentDetailTests`, `ShipmentListTests`, `HubScanTests`, `CancelShipmentTests` |
| Public erişim | E2E `06-public` (allowlist anahtarları, HTML/yanıtta PII ve iç kod yok, Referer/cookie yok, yenilenen kod 404); integration `PublicTrackingRetentionTests` (aynı 404, rate limit, güvenilir proxy) |
| Idempotency / yanıt kaybı | E2E `02-create-replay`; duman testi byte düzeyinde replay; integration `CreateShipmentIdempotencyTests` |
| Eşzamanlılık | E2E gerçek eşzamanlı tarama → 412 → yenile; integration barrier testleri |
| Inbox kurtarma | integration `Expired_lease_owner_cannot_commit_after_takeover`, `Failure_after_apply_but_before_commit_…`, `Database_outage_neither_consumes_attempts_…`, `Batch_claim_processes_receipts_in_arrival_order_…` (yeni) |
| Outbox kurtarma | integration `Crash_after_handler_before_processed_…`, `Poison_message_dead_letters_…`, `Publish_failure_never_affects_the_command_…` |
| Canlı kanal yetkisi / iptal | integration `RealtimeTests` (7): anonim/yabancı origin reddi, abonelik yetkisi, atama/rol kaldırılınca iptal, çıkış, bilet süresi, PII'siz yük |
| Yeniden bağlanma | E2E: WebSocket koparılır + el sıkışma engellenir → DB değişir → ekran eski durumda kalır → bağlanınca HTTP eşitlemesiyle doğru durum → itme yeniden çalışır |
| Yığın dayanıklılığı | Duman: API konteyneri yeniden oluşturulunca veri ve mevcut oturum cookie'si geçerli; DB durunca live 200 / ready 503, iyileşince ready 200 |
| Çalışma zamanı DB rolü | Duman: audit tablosunda UPDATE/DELETE/TRUNCATE reddi; integration `Runtime_role_cannot_delete_receipts_…` |

### Arayüzün gerçek tarayıcıda incelenmesi (F6)
`e2e/08-screens.spec.ts` gerçek Chrome'da 12 ekranı (giriş, liste, filtre-boş, oluşturma + doğrulama hataları, detay + onay paneli, istisna + çözüm paneli, özet,
koyu tema detay, tarama, yetkisiz, public, public-bulunamadı) üç genişlikte (1440 / 820 / 390 px) çizer ve belgenin yatay taşmadığını doğrular. Görüntüler
(`src/web/test-results/screens/`, git dışı) gözle incelendi: masaüstü liste/detay/istisna/tarama, tablet liste, mobil oluşturma/detay (koyu)/public.
axe (WCAG 2.x A/AA) giriş, liste, oluşturma, detay, istisna, özet (açık + koyu), tarama ve public ekranlarında ihlal bulmadı; liste → detay → iade akışı yalnız klavyeyle tamamlandı.
Otomatik tarama tüm erişilebilirlik sorunlarını yakalamaz; ekran okuyucuyla elle test yapılmadı.

## 4. Uygulanan / çalıştırılan / yalnız belgelenen / engelli

| Sınıf | Maddeler |
|---|---|
| Uygulandı **ve** bu oturumda çalıştırılarak doğrulandı | Dilim 1–10 işlevleri; Docker image; Compose yığını (HTTPS, migrator, kalıcı anahtar/DB volume'ları); React arayüzü; SignalR; migration kurulum/yükseltme; duman testi; E2E |
| Uygulandı, **uzakta çalıştırılmadı** | `.github/workflows/ci.yml` (`backend`, `web`, `e2e`). YAML ayrıştırıldı (hata/uyarı 0; izin yalnız `contents: read`; 9 `uses` satırının tamamı 40 haneli commit SHA ile sabit); adımların komutları yerelde koşuldu, `scripts/ci-e2e-stack.sh` Git Bash'te çalıştırılıp temiz DB'de E2E geçirildi. Linux runner'da `npx playwright install --with-deps` ve dosya izinleri **denenmedi** |
| Uygulandı, sınırlı doğrulandı | Vite dev proxy (yalnız Chrome, `01/02/04` E2E 8/8); `LST_WEB_HTTPS_PFX` ile HTTPS dev sunucusu denenmedi. Simülatör senaryolarından E2E'de yalnız `HappyPath`; diğerleri integration testlerinde |
| Yalnız belgelendi (kod yok) | Çok instance için SignalR backplane / dağıtık rate limit; runbook ve benchmark planı (docs/08 Dilim 11 — bu görevin kapsamı dışında) |
| Bilinçli olarak uygulanmadı | `GET /api/shipments/changes` akışı (ADR-020); kullanıcı/rol yönetim ekranı; gerçek carrier/bildirim; harita |
| Engelli | Yok |

## 5. Bu oturumda bulunan ve düzeltilen kusurlar

1. **Inbox işleme sırası (Dilim 6):** claim sorgusu `UPDATE … RETURNING` sırasına güveniyordu; partideki makbuzlar rastgele sırayla işlenip sıralı gelen akışta geçerli olaylar `Stale` oluyordu. Alınış sırasıyla işleniyor; regresyon testi eklendi.
2. **Simülatör zamanlaması (Dilim 6):** yeni oluşturulmuş gönderide tüm olaylar aynı ana düşüp `Stale` sonuçlanıyordu.
3. Arayüzde E2E'nin yakaladıkları: public sayfada gereksiz oturum sorgusu ve iptal edilen istek; sıra bağımlı "oturum sona erdi" mesajı; mobilde yatay taşma; URL güncellemesi ertelenince geri dönen filtre kontrolleri; seçenek metinleriyle kirlenen filtre etiketleri; dakikaya yuvarlanan teslimat zamanı; kod yenilenince public sayfada kalan eski veri.

## 6. Bilinen sınırlar

- Tek instance (bellek içi rate limit, SignalR grupları, bağlantı kaydı). Redis/backplane yok.
- Canlı ipuçları yalnız durum ve istisna aç/ata/çöz için üretilir; diğer değişiklikler 60 sn'lik mutabakatla görünür. Açık bağlantının yetki iptali ≤ 15 sn gecikmelidir.
- Çıkış sunucuda bileti iptal etmez (durumsuz cookie, 8 saat kayan süre).
- `return-to-sender` nedeni saklanmaz; `EventAppliedManually` çözüm kodu enum'da var ama hiçbir tipte kabul edilmez; kilit zaman aşımı kodları uçlar arasında tutarsızdır (`shipment-busy` 503 / `idempotency-request-in-progress` 409 / kod yenilemede eşlenmemiş).
- Saklama süreleri ürün varsayımıdır, yasal uygunluk kanıtı değildir.
- Kapasite/performans ölçülmedi. Bağımlılık zafiyet taraması: `npm audit --omit=dev` (0 bulgu) ve `dotnet list Lst.slnx package --vulnerable --include-transitive` (9 projede bilinen açık yok) — yalnız bu anın danışma verisini yansıtır.

## 7. Görev kaynakları ve temizlik

| Kaynak | Durum |
|---|---|
| Compose projeleri `lst-e2e`, `lst-ci`, `lst-final` (container, ağ, `*_pgdata`, `*_dpkeys`) | **Silindi** (`down -Purge` / `down -v`; yalnız bu proje adları) |
| `.local-smoke/`, `.local-e2e/`, `.local-ci/`, `.local-final/` (rastgele secret, PFX, demo parolaları) | **Silindi** |
| Testcontainers ve `lst-migcheck-*` konteynerleri | Kendiliğinden / betik tarafından silindi |
| Vite dev sunucusu (5173) | Durduruldu |
| Image'lar: `lst-api:local`, `postgres:18.6`, .NET sdk/aspnet, `testcontainers/ryuk` | **Bırakıldı** (önbellek; yerel çalıştırmayı hızlandırır). Silmek için: `docker image rm lst-api:local` |
| Git dışı çıktılar: `src/web/node_modules`, `dist`, `test-results`, `playwright-report`, `artifacts/`, `bin/`, `obj/` | Bırakıldı |
| Docker Desktop | Oturum başında kapalıydı; testler için başlatıldı ve **açık bırakıldı** |

Son durumda çalışan container, volume veya proje süreci yoktur.

## 8. Yerelde çalıştırma

```powershell
.\scripts\local-stack.ps1 init -SeedDemo
.\scripts\local-stack.ps1 up -SeedDemo -Simulation      # https://localhost:8443
# Demo parolaları: .local\seed\Seed__Passwords__<kullanıcı> dosyalarında (ekrana yazılmaz)
```

Ayrıntı ve geliştirme modu: [`../README.md`](../README.md).
