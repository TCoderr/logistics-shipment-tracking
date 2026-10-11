# ADR-018 Public takip, güvenilir proxy'li rate limit ve saklama (Dilim 8)

Durum: Kabul edildi (2026-10-10)

## Bağlam
Alıcı, oturumsuz bir linkle gönderisini izler. Kod tahmin edilememeli, yanıt kişisel veri içermemeli, link sızarsa geçersiz kılınabilmeli,
uç nokta taramaya dayanıklı olmalı. Kişisel veri ve ham carrier payload'ı süresiz saklanmamalı; ancak makbuz silmek
`tracking_events.carrier_event_receipt_id` FK'siyle çelişiyordu.

## Kararlar
1. **Kod ve yanıt:** `PublicTrackingCode` 128 bit rastgele, Crockford Base32 26 karakter. `GET /api/public/tracking/{code}` ayrı
   `PublicTrackingView` DTO'su döner (alan kümesi unit + integration contract testiyle sabit); yalnız `Applied` olaylar, genel etiketler,
   hub yerine şehir; maskeli tracking numarası (`LS48******35`). Biçimce geçersiz ve bulunamayan kod **byte düzeyinde aynı** 404 gövdesini alır
   (traceId gibi değişken alan yok).
2. **Başlıklar:** `/api/public/*` yanıtlarının tamamına (404 ve 429 dahil, rate limiter'dan önce eklenen middleware ile)
   `Cache-Control: no-store`, `Referrer-Policy: no-referrer`, `X-Robots-Tag: noindex, nofollow`, `X-Content-Type-Options: nosniff`.
3. **Log redaksiyonu:** İstek yolunu loglayan çerçeve kategorileri (`Hosting.Diagnostics`, `Routing`, `StaticFiles`, `HttpsPolicy`) kod içinde
   Warning'e sabitlenir; uygulama kodu public kodu loglamaz. Test: Debug seviyesinde yakalanan tüm loglarda kod geçmez.
4. **Rate limit:** IP başına 20 istek/dk sliding window (6 segment) + public global 600/dk; aşımda `429 rate-limited` + `Retry-After`.
   İç API'ler etkilenmez. İstemci IP'si bağlantı IP'sidir. `X-Forwarded-For` yalnız `ReverseProxy:TrustedProxies`/`TrustedNetworks`
   tanımlıysa ve yalnız bu kaynaklardan kabul edilir (`ForwardLimit = 1`). **Bulgu:** ASP.NET Core ForwardedHeaders, iki güven listesi boşsa
   her kaynağa güvenir; bu yüzden liste boşken middleware hiç eklenmez (test: sahte başlıkla limit aşılamaz). Limiter in-memory'dir (tek instance).
5. **Kod yenileme:** `POST /api/shipments/{id}/public-tracking-code/rotate` yalnız OpsManager + CSRF + If-Match; gönderi kilidi altında
   yeni kod, eski kod aynı commit'te geçersiz; audit `PublicTrackingCodeRotated` kod değeri içermez. Detay yanıtı kodu yalnız operasyon
   personeline gösterir (`publicTrackingCode`, hub görevlisine `null`).
6. **Saklama işi:** `RetentionJob` günlük 03:00 (Europe/Istanbul), advisory lock; modül görevleri sırayla:
   - PII (180 gün, terminal durumdan): ad `[redacted]`, firma/telefon/son 4/e-posta/adres satırları NULL, şehir/ilçe/posta kodu kalır;
     olay ve deneme notları NULL, iptal nedeni `[redacted]` (CHECK zorunlu); `pii_redacted_at` idempotentliği sağlar; terminal olmayanlara dokunulmaz.
   - İstisna çözüm notları (maskelenmiş gönderiler).
   - Ham carrier gövdesi + normalize payload (30 gün, yalnız işlenmiş/dead-letter makbuzlar) ve çakışma kanıtının ham gövdesi.
   - İşlenmiş outbox (7 gün; dead-letter kalır), süresi dolmuş idempotency kayıtları.
   Parti boyutu 1000; her parti kendi transaction'ı. Sonuç sayıları audit'e (`RetentionRun`). Elle tetikleme: `POST /api/operations/retention/run` (OpsManager + CSRF).
7. **Tombstone:** Carrier makbuz ve çakışma satırları **silinmez**; yalnız ham içerik temizlenir, hash'ler ve normalize kolonlar kalır.
   FK korunur; carrier + event ID tekrar ayıklaması süresizdir. Idempotency tekrar ayıklama ufku kaydın ömrüdür (48 saat): silinen anahtar yeni istek sayılır.

## Sonuçlar
Public uç nokta PII sızdırmaz ve taranamaz; sızan link yenilenir. "13 ay sonra makbuz silme" kararı iptal edildi (FK ve kanıt bütünlüğü).
Çok instance'ta rate limit için dağıtık limiter veya edge sınırı gerekir (bilinen sınır).
