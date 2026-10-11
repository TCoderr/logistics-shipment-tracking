# ADR-016 Carrier inbox: kalıcı makbuz, lease + fencing, hata sınıflandırması (Dilim 6)

Durum: Kabul edildi (2026-10-10)

## Bağlam
Carrier olayları webhook ile gelir; carrier 2xx görmezse yeniden dener. Olay kaybolmamalı, iki kez uygulanmamalı, işlenmeden "uygulandı" denmemeli.
İşleyici çok instance çalışabilir; DB kesintisi ve işçi çökmesi olayı DLQ'ya düşürmemeli. Broker/Redis yok (docs/03).

## Kararlar
1. **Modül:** `Lst.Modules.Tracking` (carriers, `carrier_event_receipts`, `carrier_event_conflicts`, webhook, inbox işleyici, simülatör).
   Referans yönü Tracking → Shipments (`IShipmentCarrierEvents`); Shipments/Operations Tracking'i bilmez (mimari test).
   Karar fonksiyonu Shipments'ta saf `CarrierEventPolicy`; Tracking yalnız taşır ve sonucu makbuza yazar.
2. **Doğrulama sırası:** rate limit (carrier kodu başına, sınırlı bölüm anahtarı) → `application/json` → ≤64 KB ham gövde → carrier aktif ve secret var →
   `X-Lst-Signature: t=…,v1=…` HMAC-SHA256(secret, `"{t}." + ham byte`), ±300 sn, birden çok `v1` ve iki aktif secret, `FixedTimeEquals` →
   sıkı JSON (yinelenen alan, string sayı, derinlik >16 red; bilinmeyen alan atlanır ve saklanmaz) → gövde `carrierCode` = route carrier → şema.
   İmza, bilinmeyen carrier, secret yokluğu ve carrier uyuşmazlığı aynı `401 invalid-signature` (neden ayrıntısı yok).
3. **Makbuz:** `INSERT … ON CONFLICT (carrier_id, external_event_id) DO NOTHING RETURNING`; commit sonrası `202 {receiptId, status:"Received"}`.
   `raw_body` + `raw_sha256` imzalı orijinal byte'lardır; `payload` (jsonb) yalnız normalize allowlist alanlarıdır (ham kanıt değildir).
   `payload_sha256` kanonik normalize alanların hash'idir: aynı ID + aynı kanonik içerik → `200 Duplicate` (biçim farkı önemsiz);
   farklı içerik → ilk makbuz değişmez, `carrier_event_conflicts`'e ham byte'larıyla kanıt, gönderi çözülebiliyorsa `CarrierEventConflict` istisnası, audit, `409`.
   Kalıcılık sağlanamazsa (transient DB hatası) `503` + `Retry-After`; ack verilmez.
4. **Durumlar:** `Received → Processing → Processed | DeadLettered`; `Processed` ⇔ `outcome` dolu (CHECK). Outcome: Applied/Duplicate/Stale/Rejected/PendingReview.
   Çakışma bir outcome değil, ayrı kanıt kaydıdır (ilk olay kendi kararıyla işlenir).
5. **Claim:** kısa transaction: `UPDATE … SET processing_status='Processing', lock_token=gen_random_uuid(), locked_until=now+lease WHERE id IN
   (SELECT … WHERE (Received AND next_attempt_at<=now) OR (Processing AND locked_until<now) … FOR UPDATE SKIP LOCKED LIMIT n) RETURNING id, lock_token`.
   Süresi dolan lease (çöken işçi) bir sonraki claim'de devralınır.
6. **Fencing:** işleme transaction'ı makbuz satırını `id + lock_token + Processing` koşuluyla `FOR UPDATE` kilitler; satır yoksa (token değişmiş) hiçbir şey yapılmaz
   (`LostLease`). Uygulama + makbuz güncellemesi (yine token koşullu) tek transaction'dır. Satır kilitliyken başka claim onu atlar; böylece lease süresi
   işleme sırasında dolsa bile iki işçi aynı olayı commit edemez. Kilit sırası makbuz → gönderi.
7. **Hata sınıflandırması:** bağlantı/ağ (Npgsql transient, Socket/IO/Timeout), SQLSTATE 08xxx, 53xxx, 57Pxx, 40001, 40P01, 55P03, 57014 → **transient**:
   lease bırakılır, `attempts` artmaz, 5 sn sonra tekrar; lease bırakılamazsa süresi dolunca devralınır. Diğer hatalar **kalıcı**: `attempts+1`,
   backoff `min(2^n·10 sn, 10 dk)`, `Workers:CarrierInbox:MaxAttempts` (varsayılan 5) sonunda `DeadLettered` + `CarrierEventDeadLettered` audit.
   `last_error` yalnız hata tipi adıdır (mesaj PII/bağlantı ayrıntısı içerebilir).
8. **Worker:** `BackgroundService`, `Workers:Enabled` (çalışma anında okunur; testlerde kapalı). Poll hatasında üstel bekleme (≤60 sn); deneme bütçesine dokunmaz.
9. **Simülatör:** yalnız `IsDevelopment() && Simulation:Enabled`; servis ve route başka ortamda kaydedilmez; Development dışı ortamda bayrak açıksa
   `ValidateOnStart` uygulamayı durdurur. `POST /api/dev/carrier-simulator/runs` (OpsManager + CSRF) olayları gerçek imzayla HTTPS loopback adresine
   (`Simulation:BaseUrl`) gönderir; yalnız loopback + `CN=localhost` geliştirme sertifikası kabul edilir. Olay zamanları gönderinin son durum zamanından
   sonraya yerleştirilir. Gerçek carrier'a istek yoktur.
10. **Yetkiler:** runtime rol `tracking.carriers` SELECT; makbuz ve çakışma tablolarında SELECT/INSERT/UPDATE (DELETE yok: kanıt silinmez,
    saklama süresinde yalnız ham gövde temizlenir — Dilim 8).

## Sonuçlar
Kayıp yok (ack = commit), çift uygulama yok (unique + fencing + iş tekrarı kontrolü gönderi kilidi altında), DB kesintisi DLQ üretmez.
Sıralama garantisi yoktur: sıra dışı olaylar karar fonksiyonunda Stale/PendingReview olur. Sequence yalnız tekrar/stale tespiti içindir;
eksik olayları yakalama (catch-up) mekanizması değildir.
