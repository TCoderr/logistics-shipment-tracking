# 06 — Güvenlik ve Hata Davranışı

> İlgili: [03 Mimari](03-architecture-decisions.md) · [05 API](05-api-and-event-contracts.md) · [09 Test](09-test-and-verification-plan.md)
> İlke: kriptografi ve token algoritmaları **icat edilmez**; platformun hazır, bakımlı bileşenleri kullanılır.

## 1. Kimlik doğrulama

### 1.1 Seçenekler

| Seçenek | Uygunluk (v1) | Not |
|---|---|---|
| **ASP.NET Core Identity + cookie** | **Seçildi** | Yerel kullanıcılar, parola hash'i (PBKDF2, Identity varsayılanı), lockout, güvenlik damgası; SPA aynı origin |
| Harici OIDC sağlayıcı (Entra ID, Keycloak, Auth0) + OpenID Connect handler | v2 / kurum SSO'su gelince | Ek altyapı veya ücretli hesap gerektirir; bu aşamada kurulmaz |
| Kendi JWT üretimi (`/token` endpoint'i + simetrik anahtar) | Reddedildi | Token iptali, rotasyon, saklama (XSS'e açık `localStorage`) sorunlarını üstlenir; ilk taraf SPA için kazancı yok |
| Identity API endpoint'lerinin bearer token'ları | Gerekirse el terminali için | Bu token'lar JWT değil, Data Protection ile korunan opak token'lardır |

**JWT bir kimlik doğrulama sağlayıcısı değildir;** yalnızca bir token biçimidir. JWT kullanılacaksa soruların cevabı bellidir: *issuance* güvenilir bir OIDC sağlayıcısında, *validation* `Microsoft.AspNetCore.Authentication.JwtBearer` ile (issuer, audience, imza anahtarı JWKS'ten, `ValidateLifetime`), *expiry* kısa ömürlü access token (≤15 dk) + sağlayıcıda refresh. v1'de bu yolun gereği olmadığı için seçilmedi.

### 1.2 Cookie ayarları

- `__Host-lst.auth`, `HttpOnly`, `Secure`, `SameSite=Strict`, `Path=/`.
- Kayan süre 8 saat (bir vardiya), mutlak üst sınır 12 saat.
- **Karar değişikliği (Dilim 1):** Cookie security-stamp aralığının rol/hub değişikliğini tek başına algıladığı iddiası kaldırıldı. Rol ve hub kapsamı her istekte **güncel DB kaydından** okunur (`IActorAccessReader`, `CurrentRoleRequirement`); cookie'deki rol claim'lerine yetki için güvenilmez. Yetki değişince oturumun düşürülmesi (security stamp güncelleme/oturum politikası) gelecekteki kullanıcı yönetimi diliminde açık iştir.
- **CSRF bootstrap (Dilim 1):** `GET /api/auth/csrf` anonim çağrılır; antiforgery cookie'si (`__Host-lst.af`, HttpOnly, Secure, SameSite=Strict) ve request token üretir, `Cache-Control: no-store`. Login dahil cookie ile durum değiştiren tüm POST'lar `X-CSRF-TOKEN` ister; SameSite=Strict bu kontrolün yerine geçmez. Login öncesi anonim token kullanılır; login sonrası token kullanıcıya bağlı olduğundan `GET /api/auth/me` ile yeni token alınır. Logout da kimlikli token ister. Token ve cookie değerleri loglanmaz. Carrier webhook ve public GET hariç.
- Yerel örnekler ve testler HTTPS kullanır; `Secure`/`__Host-` koşulları gevşetilmez (integration test istemcisinin `BaseAddress`'i `https://localhost`).
- API istemcisine login sayfasına `302` yerine `401`/`403` ProblemDetails döner.
- Lockout: 5 başarısız denemede 15 dk. Login başarısızlığı (bilinmeyen kullanıcı, hatalı parola, kilitli hesap) aynı `401 invalid-credentials` yanıtını alır; bilinmeyen kullanıcıda da sahte parola doğrulaması yapılır (yanıt süresiyle kullanıcı varlığı sızdırılmaz). **Karar (Dilim 1 doğrulaması):** `LoginFailed` audit kaydı bu bölümün güvenlik gereksinimidir ve login endpoint'i Dilim 1'dedir; bu yüzden uygulandı. Kayıt: `actor_type = Anonymous`, `actor_id = anonymous`, `entity_type = User`, `entity_id` = var olan kullanıcının id'si veya `unknown`, `outcome = Rejected`, `reason_code` ∈ {`invalid-credentials`, `locked-out`, `not-allowed`}, `changes = {}`. Kullanıcı adı, parola, istek gövdesi ve IP yazılmaz (kullanıcı adı alanına yanlışlıkla parola girilmiş olabilir); iç neden yalnız audit'tedir, HTTP yanıtı geneldir. IP prefix'i kaydı ve başarılı giriş audit'i kapsam dışı bırakıldı.
- **Karar değişikliği (Dilim 1):** Development seed parolaları `dotnet user-secrets` ile verilir (`Seed:Passwords:<kullanıcı adı>`); konsola, loga veya rapora yazılmaz, repoda parola yok. Parolası verilmeyen seed kullanıcısı oluşturulmaz.

## 2. Yetkilendirme

### 2.1 Roller ve politikalar

| Politika | Kural |
|---|---|
| `CanCreateShipment` | `OpsAgent` veya `OpsManager` |
| `CanReadShipment(resource)` | Rol iç kullanıcı **ve** kaynak kapsamı (§2.2) |
| `CanScanAtHub(hubCode)` | `HubOperator` ve kullanıcı o hub'a atanmış |
| `CanRecordAttempt(shipment)` | `OpsAgent`, veya `HubOperator` ve gönderinin `destination_hub_id`'sine atanmış |
| `CanCancel`, `CanReturnToSender` | `OpsAgent`, `OpsManager` |
| `CanAssignException(target)` | `OpsManager` herkese; `OpsAgent` yalnız kendine |
| `CanResolveException` | `OpsManager`, veya atanan `OpsAgent` |
| `CanViewAudit`, `CanRotatePublicCode`, `CanManageHubAssignments` | `OpsManager` |
| `CanRunSimulation` | `OpsManager` + Development + `Simulation:Enabled` |

Kaynak bazlı kontroller ASP.NET Core `IAuthorizationService.AuthorizeAsync(user, resource, requirement)` ile handler'larda yapılır; endpoint attribute'ları yalnız rol düzeyindedir.

### 2.2 Hub kapsamı (HubOperator)

Bir gönderi, HubOperator'ın kapsamındadır eğer kullanıcının atandığı hub'lardan biri gönderinin `origin_hub_id`, `destination_hub_id` veya `current_hub_id`'si ise.

- **Liste sorgusu** kapsam filtresini SQL'e ekler (`WHERE origin_hub_id = ANY(@hubs) OR ...`); sonuçlar uygulamada filtrelenmez (sayfalama bozulmasın ve sızıntı olmasın).
- **Detay** kapsam dışı ise `404` döner, `403` değil (gönderinin varlığı sızdırılmaz).
- **Tarama**, yolu (`/api/hubs/{hubCode}/scans`) kullanıcının atandığı hub olmadığında `403` döner; burada hub kodu zaten kullanıcının bildiği bir değerdir.
- Paket etiketi okutulduğunda gönderi kapsam dışında olsa bile tarama değerlendirilebilir (koli fiziksel olarak o hub'a gelmiştir); yanıt yalnız tarama sonucu ve gönderi durumunu içerir, PII içermez.

### 2.3 Alan düzeyinde görünürlük

| Alan | OpsAgent / OpsManager | HubOperator | Public |
|---|---|---|---|
| Alıcı adı | Tam | Tam (kapsam içinde) | Yok |
| Alıcı telefonu | Tam | Gönderi kendi hub'ında `OutForDelivery`/`DeliveryFailed` iken tam, aksi halde `+90532*****67` | Yok |
| Alıcı adres satırı | Tam | Tam (kapsam içinde; dağıtım için gerekli) | Yok |
| Şehir | Tam | Tam | Yalnız varış şehri ve olay şehirleri |
| Gönderen bilgisi | Tam | Ad + şehir | Yok |
| İç not, istisna | Var | Kendi hub istisnaları | Yok |
| Audit | OpsManager | Yok | Yok |

Maskeleme sunucuda yapılır; UI'da gizlemek güvenlik kontrolü sayılmaz.

### 2.4 SignalR grup yetkileri

- Bağlantı cookie ile kimliklenir (aynı origin); anonim bağlantı reddedilir.
- İstemci grup adını **kendisi belirleyemez**; yalnız `SubscribeHub(hubCode)`, `SubscribeShipment(id)`, `SubscribeOperations()` metotlarını çağırır. Sunucu, HTTP endpoint'leriyle **aynı** yetki servisini kullanarak kontrol eder ve grubu (`hub:{hubId}`, `shipment:{id}`, `ops`) sunucu tarafında ekler.
- **Karar değişikliği (Dilim 1):** Security stamp aralığının SignalR gruplarını temizlediği iddiası kaldırıldı. Kural: grup üyeliği sunucuda, abonelik anında güncel DB atamasıyla verilir; mesajlar PII taşımaz ve istemci veriyi HTTP ile yeniden okur (HTTP her istekte güncel DB yetkisini denetler). Atama kaldırıldığında açık bağlantıların gruplardan çıkarılması (ör. atama değişikliği olayıyla bağlantı/grup iptali) dilim 10'da tasarlanacak açık iştir. Reconnect telafisinin `change_seq` sınırı için bkz. [04 §3.2](04-database-design.md#32-shipmentsshipments).

## 3. Public takip güvenliği

| Risk | Önlem |
|---|---|
| Kod tahmini / enumeration | 128 bit rastgele kod (`2^128` uzay); tracking numarası public erişim vermez |
| Brute force | IP başına 20 istek/dk sliding window (ASP.NET Core rate limiter); global 600/dk; aşımda `429` + `Retry-After`; ardışık 404'ler log/metrik |
| Var/yok farkı ile bilgi sızıntısı | Geçersiz biçim ve bulunamayan kod aynı yanıt gövdesi ve kodu (`404`) |
| PII sızıntısı | Ayrı DTO (`PublicTrackingView`), alan allowlist'i; entity'den otomatik eşleme yok; contract testi alan listesini sabitler ([09](09-test-and-verification-plan.md)) |
| Link paylaşımı / sızan kod | Kod yenileme (E16) eskiyi geçersiz kılar; public görünüm zaten adres/telefon içermez |
| Önbellek / referrer | `Cache-Control: no-store`, `Referrer-Policy: no-referrer`, `X-Robots-Tag: noindex` |

> **Karar değişikliği (Dilim 8):** `X-Forwarded-For` yalnız açıkça güvenilen proxy/ağdan kabul edilir. ASP.NET Core ForwardedHeaders iki güven listesi
> boşken her kaynağa güvendiği için, liste boşsa middleware hiç eklenmez (integration testi sahte başlıkla limitin aşılamadığını doğrular).
> İstek yolunu loglayan çerçeve kategorileri Warning'e sabitlenmiştir; public kod log'a girmez (test Debug seviyesinde doğrular).

Rate limiter in-memory'dir (tek instance). Çok instance'a geçilirse dağıtık sınır (Redis) veya edge/WAF seviyesi sınır gerekir; bu bilinen bir sınırdır.

## 4. Webhook güvenliği

| Kontrol | Ayrıntı |
|---|---|
| Taşıma | Yalnız HTTPS |
| Provider binding | Route `{carrierCode}` → secret seçimi; gövdedeki `carrierCode` route ile aynı olmalı; olayın gönderisi o carrier'a atanmış olmalı (yoksa `Rejected: carrier-not-assigned`) |
| İmza | HMAC-SHA256, girdi `"{t}.{rawBody}"` (ham bayt), `CryptographicOperations.FixedTimeEquals` ile sabit zamanlı karşılaştırma |
| Zaman damgası | `|now − t| ≤ 300 sn`; dışında `401` |
| Replay | Zaman penceresi + `(carrier_id, external_event_id)` unique; receipt'ler 13 ay saklandığı için pencere içi replay `Duplicate`'e düşer |
| Secret rotasyonu | Carrier başına en fazla 2 aktif secret; imza başlığında birden çok `v1` |
| Boyut | Kestrel/endpoint düzeyinde 64 KB `MaxRequestBodySize`; aşımda `413`, gövde okunmaz |
| İçerik tipi | Yalnız `application/json` (`415`) |
| Şema | Sıkı DTO; bilinmeyen `eventType` `400`; string alan uzunluk sınırları; derinlik sınırı (`JsonSerializerOptions.MaxDepth = 16`) |
| Hata ayrıntısı | `401` gövdesi nedeni söylemez (imza mı zaman mı); neden yalnız güvenlik logunda |
| Rate limit | Carrier başına 50 istek/sn (konfigürasyon) |

Simülasyon kontrolü sunucudadır: `/api/dev/*` route'ları `IsDevelopment() && Simulation:Enabled` değilse **kaydedilmez**. Production'da `Simulation:Enabled=true` ise başlangıç doğrulaması (`IValidateOptions`) uygulamayı durdurur. Frontend'deki "senaryo seçici" yalnız `GET /api/auth/me` yanıtındaki `features.simulation` bayrağına göre görünür; ama gizlemek tek kontrol değildir.

## 5. Secret yönetimi ve loglama

### 5.1 Secret'lar

| Secret | Development | Production |
|---|---|---|
| DB bağlantı dizesi | `dotnet user-secrets` | Ortam değişkeni / platform secret store (ör. Key Vault, Docker secrets) |
| Carrier webhook secret'ları | `user-secrets` (`Carriers:SIMEX:Secrets:0`) | Secret store |
| Cursor HMAC anahtarı | `user-secrets` | Secret store |
| Data Protection anahtarları | Yerel dosya | Kalıcı ve paylaşılan anahtar deposu (DB veya dosya + şifreleme); konteyner yeniden başlayınca kaybolmamalı |

- Repoda secret yok; `appsettings.json` yalnız anahtar adlarını boş değerle içerir. CI'da gizli bilgi gerektiren adım yok (testler geçici DB kullanır).
- Secret'lar loglanmaz; `IOptions` nesneleri `ToString`'de secret alanlarını göstermez.

### 5.1.1 Test veritabanı güvenliği (Karar değişikliği Dilim 1)

- Tercih: her koşuda Testcontainers ile yeni `postgres:18` konteyneri; mevcut veritabanı veya konteynere migration/seed/reset/cleanup uygulanmaz.
- Yerel alternatif yalnız kullanıcının sağladığı **ayrı, silinebilir** DB ve rollerle: `LST_TEST_POSTGRES` (sahip rol), `LST_TEST_RUNTIME_POSTGRES` (çalışma zamanı rolü), `LST_TEST_ALLOW_DESTRUCTIVE=true`, `LST_TEST_RUN_ID`, DB adı `lst_test_<runId>` ve `lst_test_marker.run` tablosunda aynı `run_id`. `lst_test` öneki tek başına izin değildir. Gerçek `current_database()`, loopback sunucu ve işaret doğrulanmadan hiçbir yıkıcı işlem çalışmaz (`TestDatabaseGuard`; yanlış hedefte yazma çağrısının yapılmadığı spy ile unit test edilir).
- Cleanup yalnız koşunun oluşturduğu kaynaklara dokunur (konteyner). Bağlantı dizesi, parola, cookie ve CSRF token test çıktısına yazılmaz.
- Integration testlerinde uygulama ayrı çalışma zamanı rolüyle bağlanır; migration'ı sahip rol uygular.

### 5.2 Yapılandırılmış log ve redaksiyon

- Her log satırı: `timestamp`, `level`, `message template`, `traceId`, `userId` (varsa), `shipmentId`/`trackingNumber` (PII değil), `outcome`.
- **Loglanmayanlar:** istek/yanıt gövdeleri, ad, telefon, e-posta, adres, public tracking kodu, cookie, `Authorization`/`X-Lst-Signature` başlıkları, webhook ham payload'ı.
- `Microsoft.Extensions.Compliance.Redaction` ile `[PersonalData]` sınıflandırılmış alanlar log'a girerse redakte edilir (savunma katmanı; birincil kural loglamamak).
- HTTP logging middleware açıksa yalnız yol, durum kodu, süre; gövde kapalı. **Dilim 1:** HTTP logging middleware eklenmedi; EF Core `EnableSensitiveDataLogging(false)` açıkça ayarlı (SQL parametre değerleri loglanmaz); Npgsql hata ayrıntısı (`Include Error Detail`) kapalı. Integration testi I14 tüm log çıktısında PII, parola, CSRF token, public kod ve auth cookie'si olmadığını doğrular.
- Güvenlik olayları (`invalid-signature`, `rate-limited`, `LoginFailed`, `forbidden`) ayrı kategori ve metrikle.

### 5.3 Hata yanıtlarında güvenlik

- Üretimde `ProblemDetails.detail` iç exception mesajı içermez; beklenmeyen hata `500` + genel metin + `traceId`.
- `404` ile `403` ayrımı kapsam kuralına göre bilinçli (§2.2).
- Doğrulama hataları alan adını söyler, gönderilen değeri yankılamaz.

## 6. Hata ve arıza davranışı

| Arıza | Davranış | Telafi |
|---|---|---|
| İstemci `POST /api/shipments` yanıtını alamadı | Commit olduysa kayıt idempotency tablosunda | Aynı anahtarla tekrar → replay |
| İstemci istek ortasında koptu | `RequestAborted` commit öncesi ise transaction rollback; commit başladıysa tamamlanır | Replay |
| Webhook işlenirken süreç çöktü | Inbox kaydı `Processing` + `locked_until` geçmişte kalır | Başka worker/yeniden başlatma lease süresi sonunda devralır; `Decide` idempotent (iş tekrarı kontrolü) |
| Outbox handler'ı çalıştı ama `processed_at` yazılamadı | Mesaj tekrar işlenir | Tüketici `messageId` ile tekrarı ayıklar; SignalR ipucu zararsız |
| Optimistic concurrency çakışması (otomatik akış) | 3 deneme, 20–200 ms jitter | Tükenirse inbox `Received`'e döner, backoff |
| Optimistic concurrency çakışması (kullanıcı) | `412` + güncel `ETag` | UI güncel durumu gösterir, kullanıcı tekrar karar verir |
| Poison carrier olayı (ör. eşleme hatası) | 5 deneme sonra `DeadLettered`, metrik + log | Runbook: düzelt ve `processing_status = 'Received'` yap |
| DB erişilemez | `/health/ready` `Unhealthy`; API `503`; worker'lar backoff | Webhook carrier yeniden denemesine güvenir |
| SignalR kopukluğu | UI "Canlı güncelleme kesildi — son güncelleme 14:02" uyarısı | Yeniden bağlanınca `changes?after=` |
| Saat sapması (cihaz) | `future-timestamp` red veya `too-old` inceleme | El terminali NTP; olay `received_at` ile karşılaştırılabilir |
| Graceful shutdown | Yeni lease alınmaz, 30 sn içinde mevcut iş biter | Bitmeyen lease süresi dolunca devralınır |

### Retry politikası özeti

| Yer | Deneme | Backoff | Not |
|---|---|---|---|
| Concurrency retry (komut içi) | 3 | 20–200 ms jitter | Yalnız otomatik akışlar ve tarama |
| Carrier inbox işleme | 5 | `2^n × 10 sn`, üst 10 dk | Sonra `DeadLettered` |
| Outbox | 8 | `2^n × 5 sn`, üst 15 dk | Sonra `dead_lettered_at` |
| DB geçici hatası (Npgsql transient) | EF Core execution strategy **kullanılmaz** kullanıcı transaction'larında (idempotency claim'i ile çakışır); worker döngüsü kendi seviyesinde yeniden dener | | |

## 7. Tehdit özeti (STRIDE kısa)

| Tehdit | Örnek | Önlem |
|---|---|---|
| Spoofing | Sahte carrier olayı | HMAC + zaman + provider binding |
| Tampering | Cursor değiştirme, `If-Match` atlama | İmzalı cursor, zorunlu `If-Match` |
| Repudiation | "Ben iptal etmedim" | Append-only audit, `lst_app` rolünde silme yok |
| Information disclosure | Public ekrandan adres, log'dan telefon | Allowlist DTO, redaksiyon, 404 kapsam kuralı |
| Denial of service | Public endpoint taraması, büyük webhook gövdesi | Rate limit, 64 KB sınırı, keyset pagination, `limit ≤ 200` |
| Elevation of privilege | Hub görevlisinin başka hub'ı taraması, SignalR grubuna sızma | Kaynak bazlı yetki, sunucu tarafı grup ekleme |
