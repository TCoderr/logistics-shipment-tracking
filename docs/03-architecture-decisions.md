# 03 — Mimari Kararlar

> İlgili: [02 Domain](02-domain-and-state-machines.md) · [04 Veritabanı](04-database-design.md) · [06 Güvenlik ve hata davranışı](06-security-and-failure-behavior.md)

Her karar **ihtiyaç → seçim → daha basit alternatif → maliyet** biçiminde yazılmıştır.

## 0. Ortam kaydı (2026-10-09)

Bu tasarım aşamasında hiçbir kurulum veya migration çalıştırılmadı.

| Araç | Durum (bu makinede) | Etkisi |
|---|---|---|
| .NET SDK | **Yok** (`dotnet` PATH'te değil) | İlk uygulama diliminden önce .NET 10 SDK kurulmalı. |
| Docker | **Yok** | Testcontainers yerel olarak çalışmaz; yerel PostgreSQL ile izole test DB alternatifi gerekli ([09 §4](09-test-and-verification-plan.md#4-test-veritabanı-stratejisi)). |
| PostgreSQL (`psql`) | **Yok** | Yerel PostgreSQL 18 kurulumu veya Docker Desktop gerekecek. |
| SQL Server istemcisi | `sqlcmd` (ODBC 17) var, sunucu doğrulanmadı | Seçimi etkilemedi. |
| Node.js / npm | v24.18.0 / 12.1.0 | UI dilimi için yeterli. |
| Git | 2.55 | Çalışma dizini henüz repo değil; repo/remote oluşturulmadı. |

## ADR-01 Hedef platform: .NET 10 LTS

- **Kaynak:** [dotnet.microsoft.com .NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core) (sayfa güncellemesi 2026-09-08, 2026-10-09'da kontrol edildi).
  - .NET 10 — LTS, 2025-11-11 çıkış, destek sonu **2028-11-14**, aktif destek, son yama 10.0.12.
  - .NET 8 — LTS ama destek sonu **2026-11-10** (bakım dönemi; ~1 ay kaldı).
  - .NET 9 — STS, destek sonu 2026-11-10.
  - .NET 11 — RC1 (go-live) — henüz GA değil, LTS de olmayacak (tek numaralı sürüm STS).
- **Karar:** `net10.0`, ASP.NET Core 10. Yeni proje için tek mantıklı LTS seçeneği.
- **Paket uyumu (NuGet, 2026-10-09):**
  - `Microsoft.EntityFrameworkCore` 10.0.12 (2026-09-08) — `net10.0`.
  - `Npgsql.EntityFrameworkCore.PostgreSQL` **10.0.3** (2026-07-10) — `net10.0`, bağımlılık `Microsoft.EntityFrameworkCore >= 10.0.4 && < 11.0.0` ve `Npgsql >= 10.0.3`. EF Core 10.0.12 ile uyumlu.
  - Kural: EF Core ve Npgsql provider **aynı major** (10.x) tutulur; EF Core 11 provider'ı GA olmadan yükseltme yapılmaz.
- **Maliyet:** Kasım 2028'e kadar major yükseltme zorunluluğu yok; .NET 12 LTS (Kasım 2027 beklenen) geldiğinde planlı geçiş.

## ADR-02 Modular monolith

- **İhtiyaç:** Gönderi durumu, takip olayı, istisna ve audit çoğu komutta **aynı transaction'da** değişmeli. Tek ekip, tek deploy, tek veritabanı.
- **Seçim:** Tek ASP.NET Core süreci; modüller ayrı class library projeleri; tek PostgreSQL veritabanı, modül başına şema.
- **Mikroservis neden değil:** Modüller arası tutarlılık dağıtık transaction/saga gerektirir; ölçek veya bağımsız deploy ihtiyacı yok. Operasyon maliyeti (servis keşfi, ağ hataları, broker) portföy projesinin backend kalitesini göstermeye katkı yapmadan karmaşıklık ekler.
- **Daha basit alternatif:** Tek proje, klasör bazlı modüller. Reddedildi çünkü modül sınırlarını derleyici ve mimari testlerle zorlamak (internal görünürlük) bu projenin göstermek istediği şeylerden biri. Yine de proje sayısı küçük tutulur.
- **Maliyet:** Modüller tek `DbContext` paylaşır (ADR-05); bir modülü ayrı servise çıkarmak ileride şema ve iletişim değişikliği gerektirir.

### Modüller

| Modül | Sorumluluk | Şema | İlk dilim |
|---|---|---|---|
| `Shipments` | Gönderi aggregate'i, paket, taraf snapshot'ı, durum makinesi, timeline, teslimat denemeleri, tarama ve operatör komutları, sorgular | `shipments` | Evet (oluşturma + detay) |
| `Tracking` | Carrier webhook alımı, imza doğrulama, inbox, normalize etme, iş tekrarı, carrier adaptör arayüzü, simülatör (dev) | `tracking` | Hayır |
| `Operations` | Hub referans verisi, istisna kuyruğu, SLA/gecikme işi, KPI sorguları | `operations` | Hub tablosu evet; istisnalar hayır |
| `Identity` | ASP.NET Core Identity, roller, kullanıcı-hub ataması, yetki politikaları | `identity` | Evet |
| `Platform` (paylaşılan altyapı) | `LogisticsDbContext`, idempotency, audit, outbox, saat (`TimeProvider`), ProblemDetails eşlemesi | `platform` | Evet (idempotency + audit) |

`CarrierAdapters` ayrı modül değildir: v1'de tek bir simüle carrier olduğu için `Tracking` modülü içinde `ICarrierEventMapper` + `SimexMapper` olarak durur. İkinci gerçek carrier gelirse ayrılır.

### Proje yapısı (hedef)

```
logistics-shipment-tracking/
  src/
    Lst.Api/                     # Host: endpoint kayıtları, auth, middleware, OpenAPI, worker barındırma
    Lst.Platform/                # DbContext, idempotency, audit, outbox, ortak ProblemDetails, TimeProvider
    Lst.Modules.Shipments/       # Domain/ Application/ Infrastructure/ Endpoints/  (klasör)
    Lst.Modules.Identity/
    Lst.Modules.Operations/      # (ilgili dilimde)
    Lst.Modules.Tracking/        # (ilgili dilimde)
    web/                         # React + TS (UI diliminde)
  tests/
    Lst.UnitTests/               # domain kararları, saf fonksiyonlar, mimari kuralları
    Lst.IntegrationTests/        # gerçek PostgreSQL, API + DB
    Lst.E2ETests/                # Playwright (UI diliminde)
```

### Katman ve bağımlılık yönü (modül içinde)

```
Endpoints  ──►  Application  ──►  Domain
    │               │
    └──────►  Infrastructure (EF config, carrier HTTP, imza doğrulama)
```

- **Domain:** Saf C#. EF Core, ASP.NET, `System.Net.Http` referansı yok. Entity'ler, durum makinesi (`ShipmentStateMachine`), `Decide` fonksiyonu, değer nesneleri, domain hataları.
- **Application:** Komut/sorgu handler'ları. `LogisticsDbContext`'i doğrudan kullanır. **Generic repository yok**; EF Core zaten unit of work + repository'dir. Interface yalnız gerçek bir değişkenlik veya test sınırı varsa eklenir (`ICarrierEventMapper`, `TimeProvider`). Idempotency ve audit de doğrudan `DbContext` üzerinden çalışan somut sınıflardır.
- **Endpoints:** Minimal API grupları, HTTP ↔ komut eşlemesi, ProblemDetails.
- **Modüller arası:** Bir modül diğerinin yalnız `public` contract tiplerini (`Lst.Modules.Shipments.Contracts`) kullanır; entity'ler `internal`. Örnek: `Tracking` normalize ettiği olayı `IShipmentEventApplier.ApplyAsync(...)` ile `Shipments`'a verir; aynı `DbContext` ve transaction içinde çalışır.
- **Zorlama:** `Lst.UnitTests` içinde mimari testleri (yansıma tabanlı; ek paket gerekirse NetArchTest değerlendirilir): Domain → EF/ASP.NET referansı yok; modül A → modül B `internal` tip yok.

## ADR-03 Veritabanı: PostgreSQL 18

| Kriter | PostgreSQL 18 | SQL Server 2022/2025 |
|---|---|---|
| Lisans / yerel | Ücretsiz, Windows kurulumu ve ~100 MB Docker imajı | Developer sürümü ücretsiz; Linux konteyneri ~1.5 GB, ≥2 GB RAM |
| Optimistic concurrency | `xmin` sistem kolonu, ek kolon gerekmez (Npgsql destekli) | `rowversion` kolonu, çok iyi destek |
| Kuyruk/lease deseni | `FOR UPDATE SKIP LOCKED` | `READPAST, UPDLOCK` |
| Koşullu insert | `INSERT ... ON CONFLICT DO NOTHING` | `MERGE` / try-catch (MERGE'ün bilinen yarış sorunları) |
| Partial/filtered unique index | Var | Var (filtered index) |
| JSON | `jsonb` + GIN | `nvarchar(max)` + JSON fonksiyonları (2025'te native `json`) |
| Unique ihlalinde eşzamanlı insert | İkinci insert ilk transaction bitene kadar bekler, sonra ihlal/başarı — idempotency claim'i için doğal (ADR-07) | Benzer davranış (key-range lock), ama izolasyon seviyesine daha duyarlı |
| CI | GitHub Actions service container ile hafif | Ağır imaj, daha uzun CI süresi |
| Türkiye .NET ilanları | Giderek yaygın | Kurumsal tarafta çok yaygın |

- **Karar:** PostgreSQL 18. Belirleyici nedenler: `SKIP LOCKED` ile inbox/outbox lease'i, `ON CONFLICT`, ek kolon gerektirmeyen `xmin`, düşük yerel ve CI maliyeti.
- **SQL Server neden eklenmiyor:** İki provider'ı aynı projeye koymak test matrisini ikiye katlar ve migration'ları provider'a özel yapar; gösterilecek ek beceri yok.
- **Maliyet:** `xmin` PostgreSQL'e özgüdür; ileride SQL Server'a taşınırsa `rowversion` kolonuna geçiş gerekir. Ham SQL kullanılan iki yer (lease sorguları) provider'a özeldir ve izole edilir.

## ADR-04 Persistence: EF Core 10, Dapper yok (şimdilik)

- **Seçim:** EF Core 10 + Npgsql provider. Migration'lar EF Core ile, SQL script olarak üretilip gözden geçirilir (`dotnet ef migrations script --idempotent`).
- **Ham SQL:** Inbox/outbox lease'i (`FOR UPDATE SKIP LOCKED`) ve KPI sorguları için `FromSql`/`SqlQuery` ile parametreli SQL. Bunun için Dapper gerekmez.
- **Dapper ne zaman:** Liste/KPI sorgularında EF Core'un ürettiği SQL ölçülebilir şekilde yetersiz kalırsa (benchmark ile) okuma tarafına eklenir.
- **Adlandırma:** Tablolar/kolonlar `snake_case`. Uygulama aşamasında `EFCore.NamingConventions` paketinin EF Core 10 uyumlu sürümü doğrulanır; uyumlu değilse konvansiyon `ModelBuilder` üzerinde elle uygulanır.
- **Enum'lar:** `varchar(32)` + check constraint (okunabilir, migration'da güvenli değer ekleme). PostgreSQL native enum kullanılmaz (değer kaldırma zor).

## ADR-05 Tek `LogisticsDbContext`, modül başına şema

- **İhtiyaç:** Carrier olayı işlenirken `tracking.carrier_event_receipts`, `shipments.*`, `operations.shipment_exceptions` ve `platform.audit_entries/outbox_messages` aynı transaction'da yazılır.
- **Seçim:** `Lst.Platform` içinde tek `LogisticsDbContext`; her modül kendi `IEntityTypeConfiguration`'larını kendi assembly'sinde tutar ve başlangıçta kaydeder (`ApplyConfigurationsFromAssembly`). Modüller entity'lerine `context.Set<T>()` ile erişir.
- **Alternatif:** Modül başına `DbContext` + paylaşılan `DbConnection`/transaction. Gerçek izolasyon sağlar ama her komutta transaction paylaşımı kodu ve migration koordinasyonu gerektirir. İlk sürüm için gereksiz.
- **Maliyet:** Sınır derleyiciyle değil `internal` görünürlük + mimari testle korunur.

## ADR-06 CQRS-lite, mediator yok, event sourcing yok

- **Komutlar:** `CreateShipmentHandler`, `RecordHubScanHandler` gibi sınıflar; doğrudan DI ile endpoint'e enjekte edilir. Aggregate yüklenir, domain metodu çağrılır, `SaveChangesAsync`.
- **Sorgular:** Aggregate yüklemeden `AsNoTracking` projeksiyonla DTO'ya; liste için keyset pagination.
- **Mediator (MediatR vb.):** Pipeline davranışları (validation, transaction) burada endpoint filtreleri ve basit bir `UnitOfWork` sarmalayıcıyla çözülür. Ek paket ve lisans değerlendirmesi gerektirmez. Gerekirse sonradan eklenebilir.
- **Event sourcing:** Durum geçmişi `TrackingEvent` ile zaten append-only tutuluyor, ama güncel durum olaylardan yeniden üretilmiyor. Sıra dışı olaylarda "yeniden oynat" yaklaşımı tam da istenmeyen davranış (körlemesine timestamp sıralaması) olurdu. Gerekmez.

## 6. Transaction, outbox ve worker tasarımı

### 6.1 Transaction sınırları

| İşlem | Transaction 1 | Transaction 2 | Harici çağrı |
|---|---|---|---|
| Gönderi oluşturma | Idempotency claim + shipment + parties + packages + audit + idempotency yanıtı | — | Yok |
| Hub taraması | Shipment + package + tracking event + (istisna) + audit + outbox | — | Yok |
| Carrier webhook alımı | **Yalnız** inbox insert (veya duplicate/conflict kaydı) → commit → `202` | — | Yok |
| Carrier olayı işleme (worker) | Lease alma (kısa, ayrı) | Shipment + event + (istisna) + audit + outbox + receipt sonucu | Yok |
| Outbox dağıtımı (worker) | Lease alma (kısa) | `processed_at` / hata yazma | SignalR/bildirim **transaction dışında**, iki transaction arasında |
| SLA kontrolü (worker) | Gönderi başına küçük transaction: istisna açma + audit + outbox | — | Yok |

**Kural:** Harici sağlayıcı çağrısı (SignalR yayını, bildirim kanalı, carrier API) hiçbir zaman açık DB transaction'ı içinde yapılmaz.

### 6.2 Webhook neden "önce sakla, sonra işle"?

- `202` yalnız inbox kaydı kalıcı olduktan sonra döner; carrier'ın yeniden deneme davranışı ile birlikte olay kaybı olmaz.
- İş kuralı hatası, concurrency retry veya DB yavaşlığı carrier'ın HTTP zaman aşımına yansımaz.
- Alternatif (aynı istekte senkron işleme) daha basit görünür ama concurrency retry'ları HTTP isteği içinde tutar ve kısmi hatada "inbox'ta var mı yok mu" belirsizliği yaratır.

### 6.3 Outbox ve crash sonrası kurtarma

```
[Komut transaction'ı]  shipment değişikliği + outbox_messages INSERT  → COMMIT
          │
          ▼ (süreç burada çökse bile mesaj DB'de)
[OutboxDispatcher]  SELECT ... WHERE processed_at IS NULL AND available_at <= now()
                    AND (locked_until IS NULL OR locked_until < now())
                    ORDER BY id FOR UPDATE SKIP LOCKED LIMIT 50
                    → locked_until = now() + 60s, locked_by = instanceId → COMMIT
          │
          ▼  her mesaj için: handler (SignalR yayını / bildirim kanalı) — transaction dışı
          │
          ▼  UPDATE processed_at = now()  (veya attempts+1, available_at = backoff, last_error)
```

- **Teslim garantisi:** At-least-once. Çökme işlem ile `processed_at` yazımı arasında olursa mesaj lease süresi bitince tekrar işlenir. Handler'lar idempotent olmalı: SignalR mesajı yalnız ipucu (`shipmentId`, `changeSeq`) taşır; istemci tekrarı zararsızdır. Bildirim kanalı `MessageId` ile tekrarı ayıklar.
- **Poison mesaj:** 8 denemeden sonra `dead_lettered_at` dolar, metrik ve log üretilir, yönetici yeniden kuyruğa alabilir (v2 ekranı; v1'de SQL runbook).
- **Backoff:** `min(2^attempt × 5 sn, 15 dk)` + %20 jitter.

### 6.4 Worker'lar

| Worker | Tetik | Lease | Notlar |
|---|---|---|---|
| `CarrierInboxProcessor` | 1 sn polling + boşsa 5 sn'ye kadar geri çekilme | 60 sn | Batch 20; kayıt başına ayrı işleme transaction'ı; 5 deneme sonra `DeadLettered` |
| `OutboxDispatcher` | 1 sn polling | 60 sn | Batch 50 |
| `SlaMonitor` | 5 dk | Tek instance için PostgreSQL advisory lock (`pg_try_advisory_lock`) | Gecikmiş/hareketsiz gönderi istisnası |
| `RetentionJob` | Günde 1 (03:00 yerel) | Advisory lock | PII maskeleme, ham payload silme, idempotency/outbox temizliği; 1000'lik partiler |

- **Barındırma:** v1'de `Lst.Api` süreci içinde `BackgroundService` olarak, `Workers:Enabled` bayrağıyla. Çok instance çalışmak güvenli (lease + `SKIP LOCKED` + advisory lock). Ayrı `Lst.Worker` host'u, API ve worker'ın farklı ölçeklenmesi gerektiğinde çıkarılır.
- **CancellationToken:** Her döngü ve DB çağrısı `stoppingToken` alır. Endpoint'lerde `HttpContext.RequestAborted` handler'a geçer; ancak **commit başladıktan sonra iptal edilmez** (yarım istemci kopması işlemi geri almaz; idempotency replay bunu karşılar).
- **Zaman aşımları:** DB komut zaman aşımı 15 sn; idempotency claim için `lock_timeout = 5s`; outbox handler başına 10 sn (`CancellationTokenSource.CancelAfter`).
- **Graceful shutdown:** `HostOptions.ShutdownTimeout = 30 sn`. Worker yeni lease almayı bırakır, elindeki kaydı bitirmeye çalışır; bitiremezse lease süresi dolar ve başka instance devralır. Lease süresi (60 sn) > tek kayıt işleme üst sınırı (15 sn DB timeout + retry).

## ADR-07 Kalıcı idempotency (DB tabanlı)

- **İhtiyaç:** İstemci yanıtı kaybederse aynı isteği güvenle tekrarlayabilmeli; ikinci gönderi oluşmamalı.
- **Seçim:** `platform.idempotency_records` tablosu, gönderi ile **aynı transaction'da** claim edilir. Ayrıntılı contract [05 §3](05-api-and-event-contracts.md#3-idempotency-contractı).
- **Redis neden değil:** Redis'teki anahtar ile DB'deki gönderi atomik değildir; çökme arası tutarsızlık doğar. DB zaten var.

## ADR-08 Mesajlaşma: broker yok (v1)

| Seçenek | Gerçek ihtiyaç olurdu eğer… | v1 kararı |
|---|---|---|
| RabbitMQ | Birden çok bağımsız tüketici süreci, yüksek hacimli asenkron iş, farklı ekiplerin servisleri | Gerek yok; outbox + worker yeterli |
| Kafka | Olay akışının uzun süre saklanıp yeniden okunması, çok yüksek throughput, akış analitiği | Gerek yok |
| Redis | Dağıtık cache, SignalR backplane (çok API instance'ı), dağıtık rate limit | Gerek yok; tek instance'ta in-memory rate limit. Çok instance'a geçilirse SignalR backplane ve rate limit için ilk aday |

Outbox tablosu, ileride broker eklenirse "outbox → broker publisher" olarak aynen kullanılır; bu yüzden şimdi broker eklemek erken optimizasyondur.

## ADR-09 Gerçek zamanlı güncelleme: SignalR (sonraki dilim)

- SignalR bağlantısı **kalıcı olay deposu değildir**; yalnız "şu gönderi değişti" ipucu taşır.
- Kaynak her zaman yetkili HTTP API'dir. İstemci bağlantı koptuğunda/yeniden bağlandığında `GET /api/shipments/changes?after={cursor}` ile kaçırdıklarını alır.
- Commit, SignalR başarısına bağlı değildir: yayın outbox dağıtıcısından yapılır.
- Grup yetkileri [06 §2.4](06-security-and-failure-behavior.md#24-signalr-grup-yetkileri)'te. Tek instance'ta backplane yok; çok instance'a geçişte Redis backplane veya Azure SignalR değerlendirilir.

## ADR-10 Kimlik doğrulama: ASP.NET Core Identity + cookie (v1)

Ayrıntı [06 §1](06-security-and-failure-behavior.md#1-kimlik-doğrulama). Kısaca: SPA aynı origin'den sunulur, HttpOnly cookie + antiforgery kullanılır; JWT taşıma/saklama sorunu yaşanmaz. Kurum SSO'su gerekirse OpenID Connect handler eklenir.

## ADR-11 UI stack: React + TypeScript (Vite), aynı origin

| Seçenek | Artı | Eksi |
|---|---|---|
| Razor Pages (+ htmx) | Tek dil, sunucu render, cookie doğal, az JS | API contract'larını UI kullanmaz; SignalR + yoğun tablo etkileşimi için JS yine gerekir |
| Blazor Server | C#, bileşen modeli | Kullanıcı başına kalıcı devre (circuit); public mobil takip ekranı için ağır; bağlantı kopmasında UI donar |
| **React + TypeScript (Vite)** | UI, public API contract'larını tüketerek onları doğrular; Türkiye ilanlarında yaygın; SignalR JS istemcisi olgun | Node araç zinciri, ayrı build |

- **Karar:** React + TypeScript + Vite, minimal bağımlılık (React Router, TanStack Query, `@microsoft/signalr`). Build çıktısı `Lst.Api` tarafından statik dosya olarak sunulur (aynı origin, ayrı CORS yok).
- **Backend önceliği:** UI dilimi, backend'in ilk 5 dilimi bittikten sonra gelir. Bileşen kütüphanesi yerine küçük, kendi tablo/etiket bileşenleri; tasarım [07](07-ui-design.md).

## ADR-12 Gözlemlenebilirlik

- `ILogger` + JSON console formatter; harici log kütüphanesi şart değil. PII alanları `Microsoft.Extensions.Compliance.Redaction` ile sınıflandırılıp redakte edilir.
- Correlation: W3C `traceparent`; ProblemDetails yanıtında `traceId`.
- Metrikler: `System.Diagnostics.Metrics` — `lst.inbox.oldest_age_seconds`, `lst.outbox.pending`, `lst.ingestion.outcomes{outcome}`, `lst.idempotency.replays`. OpenTelemetry exporter, deploy hedefi belli olunca.
- Health: `/health/live` (süreç), `/health/ready` (DB bağlantısı + bekleyen migration yok).

## Seçimlerin özet maliyeti

| Seçim | İhtiyaç | Basit alternatif | Maliyet |
|---|---|---|---|
| Modular monolith | Tek transaction'da çok modül | Tek proje | 5–6 proje, mimari test bakımı |
| PostgreSQL 18 | Lease, koşullu insert, concurrency | SQL Server | Provider'a özel iki SQL sorgusu |
| EF Core 10 | Aggregate kalıcılığı, migration | Dapper | Üretilen SQL'i izleme gereği |
| DB outbox | Commit sonrası güvenilir yan etki | Commit sonrası doğrudan çağrı (kayıp riski) | Bir tablo + worker |
| DB idempotency | Kayıp yanıt sonrası güvenli tekrar | İstemcide tekrar etmemek (güvenilmez) | Bir tablo + temizlik işi |
| Cookie auth | İlk taraf SPA | JWT bearer | CSRF koruması gerekir (antiforgery) |
| React/TS | API-first UI | Razor Pages | Node build, ikinci dil |
