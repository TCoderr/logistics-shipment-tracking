# 09 — Test ve Doğrulama Planı

> İlgili: [02 Domain](02-domain-and-state-machines.md) · [06 Hata davranışı](06-security-and-failure-behavior.md#6-hata-ve-arıza-davranışı) · [08 Dilimler](08-implementation-plan.md)
> Bu belge planlanan testleri tanımlar; henüz hiçbir test yazılmamış veya çalıştırılmamıştır.

## 1. Test katmanları

| Katman | Odak | Araç | Hız hedefi |
|---|---|---|---|
| Unit | Domain kararları: `ShipmentStateMachine`, `Decide`, iş tekrarı anahtarı, SLA tarih hesabı, Luhn, public kod üretimi, kanonik JSON hash, maskeleme fonksiyonları | xUnit (v3), Shouldly veya yerleşik `Assert` (ek bağımlılık uygulama aşamasında seçilir) | Tüm suite saniyeler |
| Mimari | Domain → EF/ASP.NET referansı yok; modüller arası `internal` erişim yok | Yansıma tabanlı test (gerekirse NetArchTest) | Saniyeler |
| Integration | Gerçek PostgreSQL: constraint'ler, transaction, concurrency, idempotency claim yarışı, lease, API + yetki | xUnit + `WebApplicationFactory` + gerçek DB | Dakikalar |
| Contract | ProblemDetails biçimi, public DTO alan kümesi, webhook imza vektörleri | Integration içinde, snapshot benzeri sabit beklenti | — |
| E2E | Sınırlı kritik kullanıcı akışları (3 akış) | Playwright (dilim 9) | Dakikalar |

**Mock kuralı:** Veritabanı mock'lanmaz; EF Core InMemory provider ve SQLite **kullanılmaz** (constraint, `xmin`, `SKIP LOCKED`, partial index davranışı farklı). Saat `FakeTimeProvider` ile, rastgelelik test seed'i ile kontrol edilir.

## 2. Unit test odakları

- **Geçiş tablosu testleri (tablo güdümlü):** 9 durum × tüm tetikleyiciler × kaynaklar. Tablodaki her satır `Applied`; tablo dışındaki her kombinasyon kullanıcı komutunda `Rejected (transition-not-allowed)`, carrier'da `PendingReview`.
- **`Decide` sıralama kuralları:** §5.3'teki 8 kuralın her biri için en az bir pozitif ve bir "sonraki kurala düşen" negatif örnek. Kural önceliği testleri (ör. terminal + eski olay → `Stale`, terminal + yeni olay → `PendingReview`).
- **Paket konsolidasyonu:** 1/2, 2/2, paket farklı hub'da, paket hiç taranmamış.
- **SLA hesabı:** 14:59 / 15:00 / 15:01 kesimi, Cumartesi oluşturma, Pazar oluşturma, resmî tatil öncesi, `Express` vs `Standard`.
- **Kanonik hash:** alan sırası farkı aynı hash; boşluk farkı aynı hash; sayı biçimi (`1850.0` vs `1850`) JCS'e göre aynı; değer farkı farklı hash.
- **Maskeleme:** telefon `+905321234567` → `+90532*****67`; tracking `LS4829105735` → `LS48******35`.
- **Tracking no:** üretilen 10 000 değerin hepsi Luhn geçerli ve biçime uyar.

## 3. Test matrisi

`U` = unit, `I` = integration, `E` = E2E. "Dilim" ilk ortaya çıktığı dilimdir.

| ID | Senaryo | Katman | Beklenen | Dilim |
|---|---|---|---|---|
| **Durum geçişleri** |||||
| S1 | Geçiş tablosundaki her geçiş | U | `Applied`, doğru `status_after` | 4 |
| S2 | Tablo dışı her geçiş | U | `Rejected` / `PendingReview` | 4 |
| S3 | Çıkış öncesi iptal / çıkış sonrası iptal | U+I | `200` / `409 shipment-not-cancellable` + `returnToSenderAllowed` | 4 |
| S4 | Max deneme sonrası dağıtıma çıkış | U+I | `409 max-attempts-reached` | 5 |
| **Terminal koruma** |||||
| T1 | `Delivered` sonrası eski `InTransit` | U+I | `Stale`, timeline'da `isLate`, durum `Delivered` | 4/6 |
| T2 | `Delivered` sonrası daha yeni `DeliveryFailed` (carrier) | U+I | `PendingReview` + `CarrierEventPendingReview` istisnası | 6 |
| T3 | `Cancelled` gönderiye tarama | I | `409 transition-not-allowed`, durum değişmez | 4 |
| **Webhook** |||||
| W1 | Geçerli imza, yeni olay | I | `202`, inbox `Received`, worker sonrası `Applied` | 6 |
| W2 | Geçersiz imza / zaman penceresi dışı / route-gövde carrier uyuşmazlığı | I | `401`, inbox'a satır yok, güvenlik logu | 6 |
| W3 | Aynı event ID, aynı payload (alan sırası farklı) | I | `200 Duplicate`, yeni satır yok | 6 |
| W4 | Aynı event ID, farklı payload | I | `409`, ilk kayıt değişmez, `carrier_event_conflicts` satırı, audit, istisna | 6 |
| W5 | Farklı event ID, aynı iş olayı (pencere içi) | U+I | `Duplicate`, timeline'da tek satır | 6 |
| W6 | Pencere dışı benzer olay | U | Ayrı olay olarak değerlendirilir | 6 |
| W7 | Carrier sequence geride | U+I | `Stale` | 6 |
| W8 | Gönderi başka carrier'a atanmış | I | `Rejected (carrier-not-assigned)` | 6 |
| W9 | 64 KB üstü gövde / yanlış content-type / bilinmeyen `eventType` | I | `413` / `415` / `400` | 6 |
| **Sıra dışı / geç olay** |||||
| O1 | `OutForDelivery` önce, `ArrivedAtHub` (daha eski) sonra gelir | U+I | İkincisi `Stale`, durum `OutForDelivery` | 4/6 |
| O2 | Çevrimdışı el terminalinden saatler sonra gelen eski tarama | I | `Stale`, `received_at` ≫ `occurred_at` | 4 |
| O3 | Gelecek zamanlı olay (+20 dk) / 8 gün eski olay | U | `Rejected (future-timestamp)` / `PendingReview (too-old)` | 4 |
| **Eşzamanlılık** |||||
| C1 | İki hub görevlisi aynı gönderinin iki kolisini aynı anda tarar | I | Tek durum geçişi, iki paket konumu güncel, ikisi de `Applied` | 4 |
| C2 | Aynı koli iki terminalden aynı anda (farklı `clientScanId`) | I | Biri `Applied`, diğeri `Duplicate` (iş tekrarı) | 4 |
| C3 | Operatör iptali ile varış taraması yarışı | I | Ya iptal (`200`) ve tarama `409`, ya tarama ve iptal `412`; asla ikisi birden | 4 |
| C4 | Webhook worker ile operatör iade komutu yarışı | I | Worker yeniden değerlendirir; sonuç tutarlı, tek geçiş | 6 |
| C5 | İki kişi aynı istisnayı üzerine alır | I | Biri `200`, diğeri `412` | 5 |
| **Idempotency** |||||
| D1 | Yanıt kaybı sonrası aynı anahtar + aynı gövde | I | Aynı `201` gövdesi, `Idempotency-Replayed: true`, DB'de tek gönderi | 1 |
| D2 | Aynı anahtar + farklı gövde | I | `422 idempotency-key-reused` | 1 |
| D3 | Aynı anahtarla 10 paralel istek | I | 1 gönderi; 10 yanıtın hepsi aynı `id` (veya bir kısmı `409 in-progress`) | 1 |
| D4 | Farklı kullanıcı aynı anahtar | I | İki ayrı gönderi | 1 |
| D5 | İlk istek doğrulama hatası, düzeltilmiş gövde aynı anahtarla | I | `201` (hata yanıtı saklanmamış) | 1 |
| D6 | Süresi dolmuş anahtar | I | Yeni istek gibi işlenir | 1 |
| **Transaction rollback** |||||
| R1 | Gönderi insert'ünden sonra audit yazımında hata (test hook'u) | I | Gönderi, paketler, idempotency kaydı yok | 1 |
| R2 | Durum değişikliği sonrası outbox insert hatası | I | Durum değişmemiş, tracking event yok | 7 |
| R3 | İstisna çözümü + tetiklenen gönderi komutu reddedilir | I | İstisna hâlâ açık | 5 |
| **Restart / lease** |||||
| L1 | Inbox worker kaydı lease'ledikten sonra süreç durur | I | `locked_until` sonrası ikinci worker işler; tek `tracking_event` | 6 |
| L2 | Outbox handler sonrası, `processed_at` öncesi durma | I | Mesaj tekrar işlenir; tüketici `messageId` ile tekrarı atar | 7 |
| L3 | Graceful shutdown sırasında işlenen kayıt | I | Ya tamamlanır ya lease bırakılır; yarım yazım yok | 6 |
| L4 | Poison kayıt | I | 5 denemede `DeadLettered`, diğer kayıtlar işlenmeye devam eder | 6 |
| **Yetki** |||||
| A1 | Oturumsuz istek | I | `401` ProblemDetails (yönlendirme yok) | 1 |
| A2 | HubOperator kapsam dışı gönderi detayı | I | `404` | 1 |
| A3 | HubOperator atanmadığı hub'da tarama | I | `403` | 4 |
| A4 | HubOperator liste sorgusu | I | Yalnız kapsam; sayfa boyutu doğru | 3 |
| A5 | OpsAgent başkasına istisna atama | I | `403` | 5 |
| A6 | Antiforgery token'ı olmadan POST | I | `400`/`403` (ASP.NET Core antiforgery davranışı, testte sabitlenir) | 1 |
| A7 | Production yapılandırmasında `/api/dev/*` | I | `404`; `Simulation:Enabled=true` ise uygulama açılmaz | 6 |
| **Public / PII** |||||
| P1 | Public yanıt alan kümesi | I | Tam olarak tanımlı alanlar | 8 |
| P2 | Seed PII değerleri public yanıtta | I | Hiçbiri geçmez (string arama) | 8 |
| P3 | Geçersiz vs bulunamayan kod | I | Aynı durum kodu ve gövde | 8 |
| P4 | Rate limit | I | 21. istek `429` + `Retry-After` | 8 |
| P5 | Log çıktısında PII | I | Test log sink'inde ad/telefon/adres yok | 1+ |
| P6 | Idempotency kaydında PII | I | `response_body` alıcı/gönderen alanı içermez | 1 |
| **SignalR** |||||
| G1 | Yetkisiz `SubscribeHub` | I | Hata, gruba eklenmez | 10 |
| G2 | Kopukluk sırasında 3 değişiklik, sonra reconnect | I | `changes?after=` üç değişikliği döner | 10 |
| G3 | SignalR yayını hata verir | I | Komut commit olmuş, outbox yeniden dener | 10 |
| **E2E** |||||
| E2E-1 | Gönderi oluştur → detay | E | Tracking no ve paketler görünür | 9 |
| E2E-2 | İstisna üzerine al → çöz | E | Kuyruktan düşer, detayda çözüm görünür | 9 |
| E2E-3 | Public takip (mobil viewport) | E | Durum ve timeline var, PII yok | 9 |

## 4. Test veritabanı stratejisi

| Ortam | Yöntem |
|---|---|
| Docker var (CI, Docker Desktop'lu geliştirici) | **Testcontainers** `postgres:18` — test koleksiyonu başına bir konteyner; migration bir kez uygulanır |
| Docker yok (bu makine şu an) | Ortam değişkeni `LST_TEST_POSTGRES` ile yerel PostgreSQL 18 sunucusu. Test çalıştırıcı başlangıçta `lst_test_template` veritabanını migration'larla hazırlar; her test sınıfı `CREATE DATABASE lst_test_{guid} TEMPLATE lst_test_template` ile izole DB alır, sonunda `DROP DATABASE`. Bağlantı dizesi user-secrets veya ortam değişkeninden; üretim DB'sine işaret eden dize reddedilir (DB adı `lst_test` ile başlamalı). |
| Hiçbiri yok | Integration testleri `Skip` ile atlanır **ve test çıktısında açıkça "integration testleri çalıştırılmadı" yazar**; bu durumda dilim kabul edilmiş sayılmaz. |

Test verisi: her test kendi verisini kurar (builder'lar); paylaşılan seed yalnız hub'lar ve roller.
Eşzamanlılık testleri gerçek paralel bağlantılarla (`Task.WhenAll` + ayrı `DbContext`/HTTP istemcisi), `Barrier` ile aynı ana denk getirilir.

## 5. CI planı (dilim 2)

```
on: [push, pull_request]
permissions: { contents: read }
jobs:
  build-test (ubuntu-latest):
    - checkout (SHA sabit)
    - setup-dotnet 10.0.x (SHA sabit)
    - dotnet restore --locked-mode
    - dotnet build -c Release -warnaserror
    - dotnet format --verify-no-changes
    - dotnet test tests/Lst.UnitTests   --logger trx
    - dotnet test tests/Lst.IntegrationTests --logger trx   (Testcontainers)
    - dotnet list package --vulnerable --include-transitive  (yüksek önem → hata)
    - upload-artifact: test-results/*.trx, coverage.cobertura.xml (retention 7 gün)
  web (dilim 9'dan sonra):
    - npm ci, tsc --noEmit, eslint, vitest, vite build
    - playwright (yalnız main ve etiketli PR'larda)
```

> **Karar değişikliği (Dilim 1):** xunit.v3 4.x Microsoft.Testing.Platform (MTP) kullanır; .NET 10 SDK'da VSTest modu desteklenmediği için `global.json` içinde `"test": { "runner": "Microsoft.Testing.Platform" }` açıldı ve `Microsoft.NET.Test.Sdk`/`xunit.runner.visualstudio` kaldırıldı. Komutlar: `dotnet test --project tests/Lst.UnitTests/Lst.UnitTests.csproj` (konumsal proje yolu yerine `--project`). CI'da TRX çıktısı için MTP rapor seçeneği (`--report-xunit-trx`) dilim 2'de doğrulanacak. Integration testleri Docker yoksa **başarısız** olur, atlanmaz.

Analizörler: .NET SDK analizörleri `AnalysisLevel=latest-recommended`, `Nullable=enable`, `TreatWarningsAsErrors=true` (Release). Güvenli artifact: secret, bağlantı dizesi, DB dump'ı, ham log yüklenmez.

## 6. Manuel doğrulama (her dilim sonunda)

- OpenAPI dokümanı açılır ve yeni endpoint'ler beklenen şemada.
- `.http` dosyası (repo içinde, gizli bilgi yok) ile mutlu yol ve en az iki hata yolu elle denenir.
- Log çıktısı gözle PII kontrolü.

## 7. Kabul tanımı (Definition of Done)

1. Dilimin kabul ölçütleri otomatik testle karşılanıyor.
2. Integration testleri **gerçekten çalıştı** (atlanmadı).
3. Build uyarısız; format kontrolü temiz.
4. Yeni tablolar migration'da; check/unique constraint'ler testle doğrulandı (en az bir ihlal testi).
5. İlgili doküman bölümü güncellendi; sapma varsa dokümanda "Karar değişikliği" notu.

## 8. Gelecekteki benchmark planı

**Bu bölümde hiçbir sonuç yoktur; yük testi çalıştırılmadan kapasite veya gecikme iddiası yapılmaz.**

| Senaryo | İş yükü tanımı | Ölçülecekler |
|---|---|---|
| B1 Webhook patlaması | Sabit hızda artan carrier olayı; %10 teknik tekrar, %5 iş tekrarı, %5 sıra dışı, %1 aynı ID farklı payload | Kabul (`202`) p50/p95/p99; inbox en eski kayıt yaşı; işleme throughput'u; hata oranı; DB CPU ve kilit bekleme |
| B2 Liste sorgusu | 1M gönderi, 10M tracking event seed; filtre kombinasyonları; HubOperator kapsamlı | p95 gecikme, sorgu planı (indeks kullanımı), buffer hit oranı |
| B3 Gönderi oluşturma | Paralel istemciler, %5 aynı anahtarla tekrar | p95, idempotency claim bekleme süresi, `409 in-progress` oranı |
| B4 Sıcak gönderi çakışması | Tek gönderiye eşzamanlı tarama + webhook | Concurrency retry sayısı, tükenme oranı, son durum tutarlılığı |
| B5 Public takip | Rate limit altında ve üstünde istek | `429` doğruluğu, p95, CPU |

- Araç adayı: k6 (HTTP), NBomber (.NET). Ortam: üretime benzer ayrı makine; sonuçlar donanım, PostgreSQL ayarları ve veri boyutuyla birlikte raporlanır.
- Başarı ölçütleri iş birimiyle birlikte belirlenecek (ör. "B1'de inbox yaşı p95 < X sn"); X şu an tanımlı değildir.
