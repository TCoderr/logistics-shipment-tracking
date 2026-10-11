# 05 — API ve Olay Contract'ları

> İlgili: [02 Domain](02-domain-and-state-machines.md) · [04 Veritabanı](04-database-design.md) · [06 Güvenlik](06-security-and-failure-behavior.md)
> OpenAPI 3.1 dokümanı uygulama aşamasında `Microsoft.AspNetCore.OpenApi` ile koddan üretilecek; bu belge onun kaynağıdır.

## 1. Genel kurallar

| Konu | Karar |
|---|---|
| Taban yol | `/api` (iç), `/api/public` (anonim), `/api/carrier-webhooks` (makine), `/api/dev` (yalnız Development + bayrak) |
| Biçim | JSON, `camelCase`, enum'lar string (`"OutForDelivery"`) |
| Zaman | ISO-8601, yanıtlarda UTC `Z` ile (`2026-10-09T08:15:00Z`); istekte ofsetli değer kabul edilir, ofsetsiz değer `400` |
| Tarih | `promisedDeliveryDate`: `"2026-10-12"` (Europe/Istanbul iş günü) |
| Kimlik | `id` alanları UUID string |
| Hata | RFC 9457 ProblemDetails (`application/problem+json`) |
| Versiyon | v1'de URL versiyonu yok; kırıcı değişiklik gerekirse `/api/v2` |
| Concurrency | Durum değiştiren kullanıcı komutları `If-Match: "<etag>"` ister; detay yanıtı `ETag` döner |
| Pagination | Keyset (cursor) — offset yok |
| Kimlik doğrulama | Cookie (iç), HMAC imza (carrier), yok (public, rate limited) |

### 1.1 ProblemDetails

```json
{
  "type": "urn:lst:problem:shipment-not-cancellable",
  "title": "Gönderi iptal edilemez",
  "status": 409,
  "detail": "Gönderi hub'dan çıkış yaptığı için iptal edilemez. Göndericiye iade başlatılabilir.",
  "instance": "/api/shipments/0192f3a4-7c1e-7b2a-9c11-2f4e5d6a7b8c/cancel",
  "code": "shipment-not-cancellable",
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
  "currentStatus": "InTransit",
  "returnToSenderAllowed": true
}
```

Doğrulama hataları `400` + `errors` sözlüğü:

```json
{
  "type": "urn:lst:problem:validation",
  "title": "İstek doğrulanamadı",
  "status": 400,
  "code": "validation",
  "errors": {
    "recipient.phone": ["Telefon E.164 biçiminde olmalı (ör. +905321234567)."],
    "packages[1].weightGrams": ["1 ile 70000 arasında olmalı."]
  },
  "traceId": "..."
}
```

### 1.2 Hata kodu kataloğu

| HTTP | `code` | Ne zaman |
|---|---|---|
| 400 | `validation` | Alan doğrulama |
| 400 | `idempotency-key-required` / `idempotency-key-invalid` | Header yok / biçim hatalı |
| 400 | `csrf-validation-failed` | Cookie ile kimliklenen POST'ta `X-CSRF-TOKEN` yok/geçersiz (login ve logout dahil) |
| 401 | `unauthenticated` | Oturum yok (iç API) |
| 401 | `invalid-credentials` | Login başarısız: bilinmeyen kullanıcı, hatalı parola ve kilitli hesap aynı yanıtı alır |
| 401 | `invalid-signature` | Carrier imzası/zaman damgası geçersiz (gövde ayrıntısı yok) |
| 403 | `forbidden` | Rol yetkisi yok, ya da hub görevlisinin atanmadığı hub'da tarama |
| 404 | `not-found` | Kaynak yok **veya kapsam dışı** (varlık sızdırılmaz) |
| 409 | `transition-not-allowed` | Durum makinesi geçişe izin vermiyor |
| 409 | `shipment-not-cancellable` | Çıkış sonrası iptal |
| 409 | `packages-not-consolidated` | Tüm paketler hub'da değil |
| 409 | `max-attempts-reached` | Deneme hakkı bitti |
| 409 | `idempotency-request-in-progress` | Aynı anahtarlı istek hâlâ işleniyor |
| 409 | `carrier-event-id-conflict` | Aynı event ID, farklı payload |
| 412 | `precondition-failed` | `If-Match` eşleşmedi (yanıt güncel `ETag` içerir) |
| 413 | `payload-too-large` | Webhook veya gönderi oluşturma gövdesi > 64 KB |
| 415 | `unsupported-media-type` | `application/json` değil |
| 422 | `unknown-hub` / `hub-inactive` | Hub referansı geçersiz |
| 422 | `idempotency-key-reused` | Aynı anahtar, farklı istek gövdesi |
| 428 | `precondition-required` | `If-Match` zorunlu ama yok |
| 429 | `rate-limited` | `Retry-After` header ile |
| 503 | `commit-outcome-unknown` | COMMIT sırasında bağlantı/sunucu hatası; sonuç bilinmiyor. İstemci aynı `Idempotency-Key` ile tekrar dener |

## 2. Endpoint listesi (v1)

Sayı kullanım senaryolarıyla sınırlıdır. "Dilim" sütunu [08](08-implementation-plan.md)'deki uygulama sırasıdır.

| # | Metot ve yol | UC | Roller | Dilim |
|---|---|---|---|---|
| E1 | `GET /api/auth/csrf` · `POST /api/auth/login` · `POST /api/auth/logout` · `GET /api/auth/me` | — | Anonim / oturum | 1 |
| E2 | `POST /api/shipments` | UC-01 | OpsAgent, OpsManager | 1 |
| E3 | `GET /api/shipments/{id}` | UC-03 | Tüm iç roller (kapsamlı) | 1 |
| E4 | `GET /api/shipments` | UC-02 | Tüm iç roller (kapsamlı) | 3 |
| E5 | `POST /api/hubs/{hubCode}/scans` | UC-04 | HubOperator (atanmış hub) | 4 |
| E6 | `POST /api/shipments/{id}/cancel` | UC-06 | OpsAgent, OpsManager | 4 |
| E7 | `POST /api/shipments/{id}/delivery-attempts` | UC-05 | HubOperator (varış hub'ı), OpsAgent | 5 |
| E8 | `POST /api/shipments/{id}/return-to-sender` | UC-07 | OpsAgent, OpsManager | 5 |
| E9 | `POST /api/shipments/{id}/return-completed` | UC-07 | OpsAgent, OpsManager | 5 |
| E10 | `GET /api/exceptions` | UC-09 | OpsAgent, OpsManager | 5 |
| E11 | `POST /api/exceptions/{id}/assign` · `POST /api/exceptions/{id}/resolve` | UC-09 | OpsAgent (kendine), OpsManager | 5 |
| E12 | `POST /api/carrier-webhooks/{carrierCode}` | UC-08 | Carrier (HMAC) | 6 |
| E13 | `POST /api/dev/carrier-simulator/runs` | — | OpsManager, yalnız Development | 6 |
| E14 | `GET /api/operations/summary` | UC-10 | OpsAgent, OpsManager | 7 |
| E15 | `GET /api/public/tracking/{publicTrackingCode}` | UC-11 | Anonim | 8 |
| E16 | `POST /api/shipments/{id}/public-tracking-code/rotate` | UC-11 | OpsManager | 8 |
| E17 | `GET /api/shipments/changes?after={cursor}` + SignalR `/realtime/operations` | — | Tüm iç roller (kapsamlı) | 10 |
| E18 | `GET /api/shipments/{id}/audit` | UC-12 | OpsManager | 7 |

## 3. Idempotency contract'ı

Gönderi oluşturma (E2) ve teslimat denemesi (E7) için **zorunlu**. Hub taraması (E5) aynı amacı gövde içindeki `clientScanId` ile karşılar (el terminali çevrimdışı kuyruğu için daha doğal).

| Kural | Tanım |
|---|---|
| Header | `Idempotency-Key: <16–128 karakter, [A-Za-z0-9_-]>`. İstemci her yeni iş için yeni değer üretir (UUID önerilir). |
| Kapsam | `(actor_scope, endpoint, key)`. `actor_scope = user:{userId}`; endpoint route **şablonu** (`POST /api/shipments/{id}/delivery-attempts`) + route değerleri hash'e girer. Farklı kullanıcının aynı anahtarı çakışmaz. |
| Kanonik hash | **Karar değişikliği (Dilim 1):** RFC 8785 (JCS) uygulanmaz ve öyle etiketlenmez. Endpoint'e özgü, sürümlü kanonik gösterim kullanılır: SHA-256( `lst.create-shipment.v1` + `\n` + `POST /api/shipments` + `\n` + tipli DTO'dan üretilen sabit alan sıralı JSON ). Sıkı okuma: tanımsız ve yinelenen alan, string'den sayı, ondalıklı tam sayı reddedilir; gövde ≤ 64 KB. İsteğe bağlı alanda eksik = `null` = `""`; `countryCode` yoksa `TR`; zorunlu string'ler kırpılmaz; paket sırası anlamlıdır; `declaredValue.amount` en fazla 2 ondalık, aralık 0–9 999 999 999.99, invariant `0.00` metniyle hash'lenir (1850, 1850.0, 1850.00 aynı). Hash sürümü `request_hash_version` kolonunda saklanır; sürümü farklı kayıtla karşılaştırma yapılmaz (`422`). Kod: `CreateShipmentRequestHash`. |
| Sıra | Kimlik doğrulama → **güncel DB rolü** ile yetkilendirme → CSRF → anahtar/gövde doğrulaması → claim/replay. Anahtar kaydı yetki kazandırmaz: rolü kaldırılan kullanıcı eski anahtarla `403` alır, saklanan başarı yanıtını alamaz. |
| Claim | **Karar değişikliği (Dilim 1):** READ COMMITTED transaction, `SET LOCAL lock_timeout = '5s'`, `INSERT ... ON CONFLICT (actor_scope, endpoint, idempotency_key) DO NOTHING RETURNING id`. Satır dönerse claim bizdedir; yanıt kolonları NULL'dır ve aynı transaction'da gönderi + taraflar + paketler + `Created` olayı + audit yazıldıktan sonra doldurulur, sonra `COMMIT`. Ertelenmiş constraint trigger'ı tamamlanmamış kaydın commit edilmesini engeller. Unique ihlali yakalanıp aynı (abort olmuş) transaction'da devam edilmez. |
| Eşzamanlı aynı anahtar | İkinci isteğin `ON CONFLICT` INSERT'ü ilk transaction bitene kadar bekler. İlk commit ederse satır dönmez → yeni statement ile `SELECT ... FOR UPDATE` → hash eşitse replay, değilse `422`. İlk rollback ederse ikinci INSERT satır döndürür ve devam eder. Kayıt görünmüyorsa (eşzamanlı reclaim sildi) claim en fazla 3 kez tekrarlanır. Lock timeout (`55P03`) → rollback → `409 idempotency-request-in-progress` + `Retry-After: 1`. |
| Süresi dolmuş anahtar | `created_at`/`expires_at` enjekte `TimeProvider`'ın UTC değeriyle yazılır ve karşılaştırılır (DB `now()` karıştırılmaz). Dolmuş kayıt satır kilidi altında silinir ve aynı transaction'da yeniden claim edilir; eşzamanlı isteklerden yalnız biri yeni gönderi oluşturur, diğerleri yeni kaydı replay eder. |
| Kod çakışması | Tracking/public kod veya paket etiketi unique ihlalinde (yalnız bu index adları eşleştirilir) savepoint'e dönülür ve kod yeniden üretilir (en fazla 3 deneme); ek satır veya audit oluşmaz. |
| Replay | **Karar değişikliği (Dilim 1):** İlk yanıt DTO'su bir kez UTF-8 JSON'a serialize edilir; `response_body` `bytea` olarak saklanır (jsonb alan sırasını/biçimini korumaz). Replay aynı byte dizisini, aynı durum kodu, `Content-Type`, `Location` ve `ETag` ile döner; ek header `Idempotency-Replayed: true`; ikisinde de `Cache-Control: no-store`. `ETag`, DB'de kalıcı `xmin` değerinden üretilir; ilk yanıt, replay ve detay GET aynı değeri gösterir. İş tekrar çalışmaz; replay audit'e yazılmaz. Yanıt yalnız aynı aktöre döner ve doğrudan kaynak yetkisi gerektiren alan (PII) içermez. |
| Yeniden kullanım | Aynı anahtar + farklı hash → `422 idempotency-key-reused`. |
| Ne saklanır | Yalnız başarı yanıtları (2xx). Doğrulama/iş kuralı hataları saklanmaz: claim transaction'ı rollback olur, istemci düzeltip aynı anahtarla tekrar deneyebilir (gövde değiştiği için hash da değişir; kayıt olmadığından çakışma yoktur). |
| Saklama | 48 saat. Sonra kayıt silinir; aynı anahtar yeni istek sayılır (istemci bu süreden uzun tekrar etmemeli). |
| Yanıt kaybı senaryosu | İstemci `201`'i alamadan bağlantı koptu → aynı anahtarla tekrar → aynı `201` (aynı `id`, `trackingNumber`) + `Idempotency-Replayed: true`. Commit olmadan koptuysa → kayıt yoktur → istek normal işlenir. Commit başladıktan sonra istemci iptali işlemi geri almaz. COMMIT hata verirse başarı varsayılmaz: `503 commit-outcome-unknown`; aynı anahtarla tekrar kesin sonucu verir. |

## 4. Gönderi endpoint'leri

### 4.1 `POST /api/shipments`

İstek:

```http
POST /api/shipments
Idempotency-Key: 6f1c2a8e-3b4d-4e5f-9a0b-1c2d3e4f5a6b
Content-Type: application/json
X-CSRF-TOKEN: <antiforgery token>
```

```json
{
  "serviceLevel": "Standard",
  "externalReference": "ORD-2026-100245",
  "originHubCode": "IST-01",
  "destinationHubCode": "ANK-02",
  "sender": {
    "fullName": "Deniz Kaya",
    "companyName": "Kaya Tekstil Ltd.",
    "phone": "+902124445566",
    "email": "lojistik@kayatekstil.example",
    "address": { "line1": "Atatürk Cd. No: 12", "line2": null, "district": "Bağcılar", "city": "İstanbul", "postalCode": "34200", "countryCode": "TR" }
  },
  "recipient": {
    "fullName": "Ece Yılmaz",
    "companyName": null,
    "phone": "+905321234567",
    "email": null,
    "address": { "line1": "Kızılay Mh. 5. Sk. No: 3 D: 7", "line2": null, "district": "Çankaya", "city": "Ankara", "postalCode": "06420", "countryCode": "TR" }
  },
  "packages": [
    { "weightGrams": 2400, "lengthMm": 400, "widthMm": 300, "heightMm": 200, "contentDescription": "Tekstil" },
    { "weightGrams": 900, "lengthMm": 300, "widthMm": 200, "heightMm": 100, "contentDescription": null }
  ],
  "declaredValue": { "amount": 1850.00, "currency": "TRY" }
}
```

Yanıt `201 Created` — **bilerek dar ve PII'siz** (idempotency kaydına aynen yazılır):

```http
HTTP/1.1 201 Created
Location: /api/shipments/0192f3a4-7c1e-7b2a-9c11-2f4e5d6a7b8c
ETag: "1a2b3c"
```

```json
{
  "id": "0192f3a4-7c1e-7b2a-9c11-2f4e5d6a7b8c",
  "trackingNumber": "LS4829105735",
  "publicTrackingCode": "8M3QZ0V7K2XG9TJ4WD1R6HNB5C",
  "status": "Created",
  "serviceLevel": "Standard",
  "promisedDeliveryDate": "2026-10-12",
  "packages": [
    { "id": "0192f3a4-7c1f-7000-8000-000000000001", "sequence": 1, "labelCode": "LS4829105735-01" },
    { "id": "0192f3a4-7c1f-7000-8000-000000000002", "sequence": 2, "labelCode": "LS4829105735-02" }
  ],
  "createdAt": "2026-10-09T11:20:31Z"
}
```

Hatalar: `400 validation`, `400 idempotency-key-required`, `401`, `403`, `409 idempotency-request-in-progress`, `422 unknown-hub|hub-inactive|idempotency-key-reused`.

> `ETag` değeri `xmin`'den türetilir (opak). İstemci yalnız eşitlik için kullanır.

### 4.2 `GET /api/shipments/{id}`

Kapsam dışı veya yok → `404`. Yanıt `200` + `ETag`:

```json
{
  "id": "0192f3a4-7c1e-7b2a-9c11-2f4e5d6a7b8c",
  "trackingNumber": "LS4829105735",
  "externalReference": "ORD-2026-100245",
  "serviceLevel": "Standard",
  "status": "DeliveryFailed",
  "statusOccurredAt": "2026-10-10T09:47:00Z",
  "statusChangedAt": "2026-10-10T09:47:12Z",
  "isDelayed": false,
  "promisedDeliveryDate": "2026-10-12",
  "deliveredAt": null,
  "originHub": { "code": "IST-01", "name": "İstanbul Avrupa Aktarma", "city": "İstanbul" },
  "destinationHub": { "code": "ANK-02", "name": "Ankara Çankaya Dağıtım", "city": "Ankara" },
  "currentHub": null,
  "carrier": null,
  "deliveryAttempts": { "count": 1, "max": 3 },
  "sender": { "fullName": "Deniz Kaya", "companyName": "Kaya Tekstil Ltd.", "phone": "+902124445566", "city": "İstanbul", "district": "Bağcılar", "addressLine1": "Atatürk Cd. No: 12" },
  "recipient": { "fullName": "Ece Yılmaz", "phone": "+90532*****67", "city": "Ankara", "district": "Çankaya", "addressLine1": "Kızılay Mh. 5. Sk. No: 3 D: 7" },
  "packages": [
    { "labelCode": "LS4829105735-01", "weightGrams": 2400, "dimensionsMm": [400, 300, 200], "currentHubCode": null, "lastScanType": "OutForDelivery", "lastScannedAt": "2026-10-10T07:05:00Z" }
  ],
  "timeline": [
    { "id": 88120, "eventType": "Created", "source": "Operator", "outcome": "Applied", "occurredAt": "2026-10-09T11:20:31Z", "receivedAt": "2026-10-09T11:20:31Z", "location": null, "isLate": false },
    { "id": 88731, "eventType": "ArrivedAtHub", "source": "HubScan", "outcome": "Applied", "occurredAt": "2026-10-09T16:02:00Z", "receivedAt": "2026-10-09T16:02:03Z", "location": { "hubCode": "IST-01", "city": "İstanbul" }, "isLate": false },
    { "id": 90410, "eventType": "DeliveryFailed", "source": "HubScan", "outcome": "Applied", "occurredAt": "2026-10-10T09:47:00Z", "receivedAt": "2026-10-10T09:47:12Z", "location": { "hubCode": "ANK-02", "city": "Ankara" }, "reasonCode": "RecipientAbsent", "attemptNumber": 1, "isLate": false }
  ],
  "openExceptions": [
    { "id": "0192f9b0-...", "type": "DeliveryFailed", "severity": "Medium", "status": "Open", "openedAt": "2026-10-10T09:47:12Z", "dueAt": "2026-10-12T09:47:12Z", "assignedTo": null }
  ],
  "allowedActions": ["ReturnToSender"]
}
```

- `recipient.phone` maskesi role göre: OpsAgent/OpsManager tam görür; HubOperator yalnız gönderi kendi hub'ında `OutForDelivery` veya `DeliveryFailed` iken tam, aksi halde maskeli. Kural [06 §2.3](06-security-and-failure-behavior.md#23-alan-düzeyinde-görünürlük).
- `allowedActions` sunucuda durum makinesi + rol + kapsamdan hesaplanır; UI butonları buna göre görünür. Sunucu yine de her komutu yeniden doğrular.
- `timeline` `occurredAt` + `id`'ye göre sıralıdır; `Stale` olaylar `isLate: true` ve `outcome: "Stale"` ile yer alır.

### 4.3 `GET /api/shipments`

Sorgu parametreleri:

| Parametre | Örnek | Not |
|---|---|---|
| `q` | `LS4829105735`, `LS4829105735-02`, `ORD-2026-100245`, `4567` | Tracking no / etiket / harici referans tam eşleşme; 4 haneli sayı → alıcı telefon son 4 (en az bir başka filtreyle) |
| `status` | `status=OutForDelivery&status=DeliveryFailed` | Çoklu |
| `hubCode` | `ANK-02` | Origin, current veya destination eşleşir |
| `carrierCode` | `SIMEX` | |
| `delayed` | `true` | |
| `hasOpenException` | `true` | |
| `createdFrom` / `createdTo` | `2026-10-01T00:00:00+03:00` | |
| `sort` | `-createdAt` (varsayılan), `promisedDeliveryDate`, `-statusChangedAt` | Yalnız indeksli alanlar |
| `limit` | 50 (varsayılan), en fazla 200 | |
| `cursor` | opak string | Önceki yanıttan |

Yanıt:

```json
{
  "items": [
    {
      "id": "0192f3a4-...", "trackingNumber": "LS4829105735", "status": "DeliveryFailed",
      "serviceLevel": "Standard", "promisedDeliveryDate": "2026-10-12", "isDelayed": false,
      "originHubCode": "IST-01", "destinationHubCode": "ANK-02", "currentHubCode": null,
      "carrierCode": null, "recipientCity": "Ankara", "packageCount": 2,
      "openExceptionCount": 1, "statusChangedAt": "2026-10-10T09:47:12Z"
    }
  ],
  "nextCursor": "eyJrIjpbIjIwMjYtMTAtMDlUMTE6MjA6MzFaIiwiMDE5MmYzYTQtLi4uIl19",
  "limit": 50
}
```

Cursor, sıralama anahtarı + `id`'nin base64url JSON'udur ve HMAC ile imzalanır (istemcinin cursor'ı değiştirip başka sorgu kurması engellenir). Toplam kayıt sayısı v1'de dönmez (büyük tabloda `COUNT(*)` maliyeti); UI "daha fazla yükle" kullanır.

> **Karar değişikliği (Dilim 3):** Cursor ayrı bir HMAC anahtarı yerine ASP.NET Core **Data Protection** ile korunur (amaç `Lst.Shipments.ListCursor.v1`; anahtarlar zaten kalıcı ve dönüşümlü, ek secret gerekmez). Yük: sürüm, sıralama, konum anahtarı (UTC tick veya `DayNumber`; timestamptz mikro saniye kaybı yok), tie-breaker `id` ve filtre+sıralama+kullanıcı özeti. Kurcalanmış/okunamayan cursor `400 invalid-cursor`, başka filtre/sıralama/kullanıcıya ait cursor `400 cursor-mismatch`. Tarih aralığı yarı açıktır: `createdFrom` dahil, `createdTo` hariç; ofsetsiz değer `400`. Sıralama allowlist'i: `-createdAt` (varsayılan), `createdAt`, `promisedDeliveryDate`, `-promisedDeliveryDate`, `statusChangedAt`, `-statusChangedAt`. Bilinmeyen (biçimi geçerli) `hubCode` boş sonuç verir. `carrierCode` ve `hasOpenException` filtreleri ilgili tablolar eklenince (dilim 5/6) etkinleşir; o zamana kadar öğede `carrierCode: null`, `openExceptionCount: 0` gerçek durumu yansıtır. `q` için tam eşleşme; 4 haneli telefon araması başka filtre olmadan `400`.

### 4.4 `POST /api/hubs/{hubCode}/scans`

```json
{
  "clientScanId": "HT-0042-20261009-000187",
  "labelCode": "LS4829105735-01",
  "scanType": "Arrival",
  "occurredAt": "2026-10-09T19:02:00+03:00"
}
```

Yanıt `200` (tarama bir komut sonucudur; yeni kaynak URI'si yok):

```json
{
  "outcome": "Applied",
  "reasonCode": null,
  "shipment": { "id": "0192f3a4-...", "trackingNumber": "LS4829105735", "status": "AtHub", "currentHubCode": "IST-01" },
  "package": { "labelCode": "LS4829105735-01", "currentHubCode": "IST-01" },
  "consolidation": { "packagesAtHub": 1, "packageCount": 2 }
}
```

- `Duplicate` → `200`, `outcome: "Duplicate"` (aynı `clientScanId` veya iş tekrarı).
- İş kuralı reddi → `409` ProblemDetails (`packages-not-consolidated`, `transition-not-allowed`, `max-attempts-reached`) + `outcome: "Rejected"` uzantısı.
- Kullanıcı bu hub'a atanmamış → `403`.
- Bilinmeyen etiket → `404`.
- `Stale` (el terminali çevrimdışı kuyruğundan geç gelen eski tarama) → `200`, `outcome: "Stale"`.

### 4.5 Operatör komutları

`POST /api/shipments/{id}/cancel` — `If-Match` zorunlu.

```json
{ "reason": "Gönderici siparişi iptal etti; koli hub'a teslim edilmedi." }
```

`200` + yeni `ETag` + gönderi özeti. Hatalar: `409 shipment-not-cancellable` (`returnToSenderAllowed` ipucu), `412`, `428`.

`POST /api/shipments/{id}/delivery-attempts` — `Idempotency-Key` zorunlu (`If-Match` gerekmez: deneme fiziksel bir olgudur, iş tekrarı kuralı korur).

```json
{ "outcome": "Failed", "failureReason": "RecipientAbsent", "occurredAt": "2026-10-10T12:47:00+03:00", "note": null }
```

`201` + `Location: /api/shipments/{id}/delivery-attempts/1`.

`POST /api/shipments/{id}/return-to-sender` — `If-Match`, `{ "reason": "..." }`. `POST /api/shipments/{id}/return-completed` — `If-Match`, `{ "occurredAt": "..." }`.

### 4.6 İstisnalar

`GET /api/exceptions?status=Open&status=Assigned&type=DeliveryFailed&hubCode=ANK-02&assignedTo=me&overdue=true&sort=dueAt&cursor=...`

```json
{
  "items": [
    {
      "id": "0192f9b0-...", "type": "MaxAttemptsReached", "severity": "High", "status": "Assigned",
      "shipment": { "id": "0192f3a4-...", "trackingNumber": "LS4829105735", "status": "DeliveryFailed" },
      "hubCode": "ANK-02", "occurrenceCount": 3,
      "openedAt": "2026-10-13T10:02:00Z", "dueAt": "2026-10-13T14:02:00Z", "isOverdue": true,
      "assignedTo": { "id": "…", "displayName": "M. Arslan" }
    }
  ],
  "nextCursor": null
}
```

`POST /api/exceptions/{id}/assign` — `If-Match`, `{ "assigneeUserId": "…" }` (OpsAgent yalnız kendini atayabilir).
`POST /api/exceptions/{id}/resolve` — `If-Match`:

```json
{ "resolutionCode": "ReturnToSenderInitiated", "note": "Alıcıya 3 kez ulaşılamadı, gönderici iade istedi." }
```

`ReturnToSenderInitiated` / `EventAppliedManually` çözümü, gönderi komutunu aynı transaction'da çalıştırır ve gönderinin güncel `ETag`'ini **sunucu tarafında** okur; gönderi o arada değiştiyse `409 transition-not-allowed` ile çözüm de geri alınır.

## 5. Carrier webhook contract'ı

### 5.1 İstek

```http
POST /api/carrier-webhooks/SIMEX
Content-Type: application/json
X-Lst-Signature: t=1791534900,v1=5d41402abc4b2a76b9719d911017c592...
```

- `t`: Unix saniye. `v1`: `hex(HMAC-SHA256(secret, "{t}.{rawBody}"))`.
- Ham bayt üzerinde doğrulanır (model binding'den **önce**, `EnableBuffering` + 64 KB sınırı).
- Birden çok `v1=` değeri kabul edilir (secret rotasyonu).

```json
{
  "eventId": "simex-evt-000918273",
  "carrierCode": "SIMEX",
  "trackingNumber": "LS4829105735",
  "eventType": "DELIVERY_FAILED",
  "occurredAt": "2026-10-10T12:47:00+03:00",
  "sequence": 7,
  "location": { "facilityCode": "ANK-SX3", "city": "Ankara" },
  "attempt": { "number": 1, "reason": "RECIPIENT_ABSENT" }
}
```

Şema allowlist'i: bilinmeyen üst düzey alanlar reddedilmez ama saklanmaz/işlenmez; `eventType` yalnız aşağıdaki değerler. `carrierCode` route ile eşleşmezse `401` (provider binding).

| Carrier `eventType` | `TrackingEventType` |
|---|---|
| `PICKED_UP` | `HandedToCarrier` |
| `IN_TRANSIT` | `CarrierInTransit` |
| `ARRIVED_AT_FACILITY` | `ArrivedAtHub` (konum = carrier facility) |
| `DEPARTED_FACILITY` | `DepartedHub` |
| `OUT_FOR_DELIVERY` | `OutForDelivery` |
| `DELIVERED` | `Delivered` |
| `DELIVERY_FAILED` | `DeliveryFailed` (+ `attempt` zorunlu) |
| `RETURN_INITIATED` | `ReturnInitiated` |
| `RETURNED_TO_SENDER` | `ReturnedToSender` |

Carrier reason eşlemesi: `RECIPIENT_ABSENT→RecipientAbsent`, `ADDRESS_NOT_FOUND→AddressNotFound`, `REFUSED→RecipientRefused`, `NO_ACCESS→AccessRestricted`, `DAMAGED→PackageDamaged`, diğer → `Other`.

### 5.2 Yanıtlar

| Durum | HTTP | Gövde |
|---|---|---|
| Yeni olay, inbox'a yazıldı | `202 Accepted` | `{ "receiptId": "…", "status": "Received" }` |
| Aynı event ID + aynı payload | `200 OK` | `{ "receiptId": "…", "status": "Duplicate" }` |
| Aynı event ID + farklı payload | `409` | ProblemDetails `carrier-event-id-conflict` + `receiptId` (carrier tekrar denememeli) |
| İmza/zaman/provider hatası | `401` | Minimal ProblemDetails, neden ayrıntısı yok |
| Gövde > 64 KB | `413` | |
| JSON/şema hatası | `400` | Alan adı düzeyinde hata, payload yankılanmaz |
| Rate limit | `429` + `Retry-After` | |

İş sonucu (`Applied`/`Stale`/`PendingReview`/`Rejected`) asenkron belirlenir ve carrier'a dönmez; operasyon panelinde ve `tracking.carrier_event_receipts`'te görülür. Carrier `2xx` dışı her yanıtta (409 hariç) yeniden denemelidir; tekrarlar `Duplicate` olarak güvenle emilir.

> **Karar değişikliği (Dilim 6):** Doğrulama sırası rate limit → içerik tipi → 64 KB → carrier/secret → imza → sıkı JSON (yinelenen alan, string sayı,
> derinlik > 16 → 400) → gövde/route carrier bağı (401) → şema (400). "Aynı payload" kanonik normalize alanlarla belirlenir (biçim/boşluk/bilinmeyen alan
> farkı `Duplicate`). Kalıcılık sağlanamazsa `503 service-unavailable` + `Retry-After` (ack yok). Operatör uçları eklendi:
> `POST /api/shipments/{id}/carrier` (`{ "carrierCode": "SIMEX" }`, If-Match, OperationsStaff; yalnız `Created` veya çıkış yapmamış `AtHub` ve devir öncesi),
> `GET /api/shipments/{id}/carrier-receipts` ve `GET /api/carrier-receipts?status=DeadLettered|Conflicted|PendingReview` (OperationsStaff; ham gövde dönmez).

### 5.3 Simülatör (yalnız Development)

`POST /api/dev/carrier-simulator/runs`

```json
{ "trackingNumber": "LS4829105735", "scenario": "OutOfOrderAfterDelivery" }
```

Senaryolar: `HappyPath`, `DuplicateDelivery` (aynı event ID iki kez), `BusinessDuplicate` (farklı ID, aynı olay), `OutOfOrderAfterDelivery`, `LateArrival` (saatler sonra eski olay), `SameIdDifferentPayload`, `ContradictsTerminal`, `InvalidSignature`.
Yanıt: `{ "runId", "scenario", "deliveries": [{ "eventId", "eventType", "httpStatus", "status" }] }` — yalnız gönderim sonucu; iş sonucu inbox'ta asenkron belirlenir.
Olay zamanları gönderinin son durum zamanından sonraya yerleşir; teslim içeren senaryolar devir yapılmamış gönderide önce `PICKED_UP` gönderir.
Simülatör olayları **gerçek HTTP pipeline'ından** (imza dahil) kendi webhook endpoint'ine gönderir. Endpoint ve servis kaydı yalnız `Environment = Development` **ve** `Simulation:Enabled = true` iken yapılır; aksi halde route hiç yoktur (`404`). Production'da bayrak `true` ise uygulama açılmaz.

## 6. Public tracking

`GET /api/public/tracking/{publicTrackingCode}`

```json
{
  "trackingNumberMasked": "LS48******35",
  "status": "OutForDelivery",
  "statusLabel": "Dağıtımda",
  "promisedDeliveryDate": "2026-10-12",
  "isDelayed": false,
  "packageCount": 2,
  "lastUpdatedAt": "2026-10-12T06:58:00Z",
  "destinationCity": "Ankara",
  "timeline": [
    { "occurredAt": "2026-10-09T11:20:31Z", "label": "Gönderi kaydı oluşturuldu", "city": null },
    { "occurredAt": "2026-10-09T16:02:00Z", "label": "Aktarma merkezine ulaştı", "city": "İstanbul" },
    { "occurredAt": "2026-10-09T21:40:00Z", "label": "Aktarma merkezinden çıktı", "city": "İstanbul" },
    { "occurredAt": "2026-10-10T05:15:00Z", "label": "Aktarma merkezine ulaştı", "city": "Ankara" },
    { "occurredAt": "2026-10-10T07:05:00Z", "label": "Dağıtıma çıktı", "city": "Ankara" },
    { "occurredAt": "2026-10-10T09:47:00Z", "label": "Teslim edilemedi, yeniden denenecek", "city": "Ankara" },
    { "occurredAt": "2026-10-12T06:58:00Z", "label": "Dağıtıma çıktı", "city": "Ankara" }
  ]
}
```

**Asla dönmeyen alanlar:** alıcı/gönderen adı, telefon, e-posta, adres satırları, ilçe, posta kodu, harici referans, hub iç kodu ve adı, carrier facility kodu, iç not, istisna, kullanıcı bilgisi, `Stale`/`PendingReview` olaylar (public timeline yalnız `Applied` olayları gösterir), deneme başarısızlık nedenleri (yalnız "Teslim edilemedi, yeniden denenecek" gibi genel etiket).

- Bulunamadı/geçersiz biçim → aynı `404` gövdesi (`not-found`), aynı yanıt yolu.
- `Cache-Control: no-store`; `Referrer-Policy: no-referrer`.
- Rate limit: IP başına 20 istek/dk (sliding window) + global; aşımda `429`.

> **Karar değişikliği (Dilim 8):** 404 gövdesi sabittir (`{"type":"urn:lst:problem:not-found","title":"Gönderi bulunamadı","status":404,"code":"not-found"}`,
> traceId yok) ve biçimce geçersiz/bulunamayan kod için byte düzeyinde aynıdır. Tüm `/api/public/*` yanıtlarına (404/429 dahil) ek olarak
> `X-Robots-Tag: noindex, nofollow` ve `X-Content-Type-Options: nosniff`. Zaman çizelgesinde aynı etiket + şehirli ardışık olaylar (çok paketli varış)
> tek satırdır. `destinationCity` alıcı şehridir. İstemci IP'si yalnız `ReverseProxy:TrustedProxies|TrustedNetworks` tanımlıysa `X-Forwarded-For`'dan
> alınır; limitler `RateLimiting:Public:PerIpPerMinute|GlobalPerMinute`. Kod yenileme: `POST /api/shipments/{id}/public-tracking-code/rotate`
> (OpsManager, CSRF, If-Match) → `200 { shipmentId, publicTrackingCode }` + yeni `ETag`; `412`/`428`/`404`. Gönderi detayına `publicTrackingCode`
> (yalnız OpsAgent/OpsManager; hub görevlisine `null`) ve `piiRedacted` eklendi. Saklama işini elle tetikleme: `POST /api/operations/retention/run`
> (OpsManager, CSRF) → `{ affected: { görev: satır }, total }`; başka instance çalışıyorsa `409 retention-running`
> ([ADR-018](adr/ADR-018-public-tracking-rate-limit-retention.md)).

## 7. Değişiklik akışı ve SignalR (dilim 10)

`GET /api/shipments/changes?after=104233&limit=200`

```json
{
  "items": [ { "id": "0192f3a4-...", "changeSeq": 104240, "status": "Delivered", "statusChangedAt": "2026-10-12T09:12:44Z" } ],
  "nextCursor": "104240",
  "resyncRequired": false
}
```

Yalnız kullanıcının kapsamındaki gönderiler döner. Cursor 7 günden eskiyse `resyncRequired: true` → istemci listeyi baştan yükler.

SignalR hub `/realtime/operations` (`OperationsRealtimeHub`):

| Yön | Ad | Yük |
|---|---|---|
| İstemci → sunucu | `SubscribeHub(hubCode)` | Sunucu kullanıcı-hub atamasını doğrular |
| İstemci → sunucu | `SubscribeShipment(shipmentId)` | Sunucu `GET /api/shipments/{id}` ile aynı kapsam kontrolünü yapar |
| İstemci → sunucu | `SubscribeOperations()` | Yalnız OpsAgent/OpsManager |
| Sunucu → istemci | `ShipmentChanged` | `{ shipmentId, changeSeq, status }` — PII yok |
| Sunucu → istemci | `ExceptionChanged` | `{ exceptionId, shipmentId, status }` |

İstemci mesaj aldığında ilgili kaynağı HTTP ile yeniden okur. Yeniden bağlanınca önce `changes?after=` ile boşluğu kapatır, sonra aboneliklerini yeniler.

## 8. İç olay (outbox) contract'ları

| `type` | Payload | Tüketici |
|---|---|---|
| `shipment.status-changed.v1` | `{ shipmentId, changeSeq, statusBefore, statusAfter, occurredAt, hubId }` | SignalR yayıncısı, bildirim (simüle) |
| `shipment.exception-opened.v1` | `{ exceptionId, shipmentId, type, severity, hubId }` | SignalR |
| `shipment.exception-resolved.v1` | `{ exceptionId, shipmentId, resolutionCode }` | SignalR |
| `notification.recipient-update.v1` | `{ shipmentId, template: "OutForDelivery" \| "Delivered" \| "DeliveryFailed" }` | Simüle bildirim kanalı (log); alıcı iletişim bilgisi tüketici tarafında DB'den okunur, mesaja yazılmaz |

Tüm payload'lar `messageId` (UUID) taşır; tüketiciler bununla tekrarı ayıklar.

> **Karar değişikliği (Dilim 7):** `messageId` payload içinde değil `outbox_messages.message_id` kolonundadır ve handler zarfıyla (`OutboxEnvelope`) iletilir.
> `shipment.status-changed.v1` payload'ı: `{ shipmentId, statusBefore, statusAfter, occurredAt, hubIds[] }` — `changeSeq` yok (UPDATE trigger'ında
> commit anında üretilir), tek `hubId` yerine çıkış/varış/güncel hub kimlikleri (yetkili grup hedefleme için). Oluşturma da `statusBefore: null` ile
> mesaj üretir. `notification.recipient-update.v1` yalnız `{ shipmentId, template }`; simüle kanal hiçbir şey göndermez, `platform.notification_deliveries`'e
> message_id tekil kayıt düşer. Mesajlar EF interceptor'ları ile aynı SaveChanges'ta yazılır ([ADR-017](adr/ADR-017-transactional-outbox-sla-and-kpi.md)).
> Yönetim: `GET /api/operations/outbox?status=DeadLettered|Pending`, `POST /api/operations/outbox/{messageId}/replay` (OpsManager + CSRF, audit'li).

> **Karar değişikliği (Dilim 9):** Arayüz için salt-okunur destek uçları eklendi: `GET /api/reference/hubs` (tüm iç roller;
> `{ items: [{ code, name, city, isActive }] }`), `GET /api/reference/carriers` (OpsAgent/OpsManager; `{ items: [{ code, name, isActive }] }`),
> `GET /api/exceptions?shipmentId=<uuid>` filtresi (geçersiz değer `400 validation`; filtre cursor parmak izine dahildir) ve
> `GET /api/dev/carrier-simulator` (yalnız Development + `Simulation:Enabled`, OpsManager; `{ carrierCode, scenarios[] }` — arayüz demo
> panelini bu uç yanıt verirse gösterir). React SPA aynı origin'den sunulur; `/api`, `/health`, `/realtime`, `/openapi` dışındaki GET istekleri
> istemci kabuğuna (`index.html`) döner. Gerçek sözleşmeden sapmalar (ör. `return-completed` gövde okumaz, `timeline[].attemptNumber` yok,
> `openExceptions[].assignedToUserId`) istemci tiplerinde koda göre uygulanmıştır ([ADR-019](adr/ADR-019-spa-hosting-ui-stack-and-e2e.md)).

> **Karar değişikliği (Dilim 10):** §7'deki `GET /api/shipments/changes?after=` akışı ve `changeSeq` **uygulanmadı** (sıra numarası commit sırası
> değildir; kayıpsız yakalama garantisi verilemez). Yerine: SignalR hub'ı `/realtime/operations` (cookie kimliği, aynı origin; anonim `401`, yabancı
> `Origin` `403`). İstemci → sunucu: `SubscribeOperations()` (OpsAgent/OpsManager), `SubscribeHub(hubCode)` (yalnız o hub'a atanmış kullanıcı),
> `SubscribeShipment(shipmentId)` / `UnsubscribeShipment(shipmentId)` (detay ile aynı kapsam; yetkisiz = bulunamadı). Sunucu → istemci:
> `ShipmentChanged { shipmentId, status }`, `ExceptionChanged { exceptionId, shipmentId, status: Open|Assigned|Resolved }`,
> `SubscriptionRevoked { kind: Operations|Hub|Shipment, key }`. Mesajlar outbox'tan, commit sonrası, en-az-bir-kez ve sırasız gelebilir; istemci
> (yeniden) bağlanınca ve periyodik olarak etkin sorguları HTTP ile yeniden okur. Yeni outbox tipi: `shipment.exception-assigned.v1`
> `{ exceptionId, shipmentId, hubId }`. Açık bağlantıların yetkisi `Realtime:RevalidateSeconds` (varsayılan 15) aralığıyla yeniden doğrulanır;
> çıkış kullanıcının bağlantılarını kapatır ([ADR-020](adr/ADR-020-realtime-invalidation-and-resync.md)).
