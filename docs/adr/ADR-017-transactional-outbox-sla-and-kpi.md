# ADR-017 Transactional outbox, SLA izleyici ve KPI özeti (Dilim 7)

Durum: Kabul edildi (2026-10-10)

## Bağlam
Commit sonrası yan etkiler (canlı panel invalidation, simüle alıcı bildirimi) iş değişikliğiyle birlikte kaybolmamalı ve commit'i
beklememeli; gecikme/hareketsizlik istisnaları otomatik açılmalı; KPI'lar gerçek DB toplamından gelmeli. Broker/Redis yok.

## Kararlar
1. **Yazım tek merkezden:** `ShipmentOutboxInterceptor` (Shipments) ve `ExceptionOutboxInterceptor` (Operations) EF `SavingChanges` aşamasında
   durum değişikliği/oluşturma ve istisna açılış/çözümü için `platform.outbox_messages` satırını **aynı SaveChanges/transaction**'a ekler.
   Komut yolları mesajı unutamaz; outbox INSERT hatası tüm değişikliği geri alır (test: durum, timeline ve audit değişmez).
2. **PII yok:** Payload'lar `IOutboxPayload` (statik `MessageType`) kayıtlarıdır; yalnız kimlik/kod/durum/zaman/hub kimlikleri taşır.
   Mimari test tüm payload tiplerinin özellik tiplerini ve adlarını (ad, telefon, adres, not…) denetler. `messageId` ayrı kolondadır.
   `shipment.status-changed.v1` payload'ı `changeSeq` taşımaz (değer UPDATE trigger'ında commit anında üretilir); tüketici HTTP ile yeniden okur.
3. **Dağıtıcı:** claim kısa transaction (`FOR UPDATE SKIP LOCKED`, lease 60 sn, yeni `lock_token`); handler'lar transaction dışında,
   10 sn zaman aşımıyla; sonuç `id + lock_token` koşuluyla yazılır (fencing — eski işçi sonucu yazamaz). At-least-once: handler sonrası
   çökmede mesaj lease dolunca yeniden işlenir; tüketici `messageId` ile ayıklar (simüle bildirim kanalı `platform.notification_deliveries`,
   PK = message_id).
4. **Handler yoksa işlenmiş sayılmaz:** `no-handler` hatası olarak deneme sayılır, sonunda dead-letter olur; handler eklendikten sonra replay edilir.
5. **Hata ve tekrar:** handler hatası `attempts+1`, backoff `min(2^n×5 sn, 15 dk)` + en fazla %20 jitter, 8. denemede `dead_lettered_at` + audit
   `OutboxMessageDeadLettered`. Transient DB hatası (`TransientFailures`, inbox ile ortak) deneme sayısını artırmaz.
6. **Replay:** `POST /api/operations/outbox/{messageId}/replay` yalnız OpsManager + CSRF; yalnız dead-letter mesaj (aksi `409`), satır kilidi altında
   sıfırlanır, `replay_count` artar, audit `OutboxMessageReplayed` (önceki deneme ve hata tipiyle). Liste: `GET /api/operations/outbox?status=DeadLettered|Pending`.
7. **Canlı yayın sınırı:** `IRealtimeNotifier` varsayılanı no-op; Dilim 10'da SignalR uygulamasıyla değiştirilir. Handler kayıtlı olduğundan mesajlar işlenir.
8. **SLA izleyici:** `SlaMonitor` 5 dakikada bir; PostgreSQL `pg_try_advisory_lock` ile tek çalıştırıcı. Aday sorgusu gerekli koşulu (gecikme veya
   48 saat duvar saati hareketsizlik) SQL'de, kesin kararı gönderi kilidi altında C#'ta verir. `DeliveryOverdue`: terminal/iade dışı ve
   `promised_delivery_date < bugün (Europe/Istanbul)`; 2+ gün gecikmede High (yalnız yükselir, tekrar sayısı artmaz). `NoMovement`: son
   uygulanmış olaydan bu yana **iş günü saatleriyle** en az 48 (Pazar ve `Sla:Holidays` sayılmaz; `TimeProvider`). Koşul kalkınca `AutoResolved`.
   Tekrar çalıştırma değişiklik üretmez; iki eşzamanlı izleyici tek kayıt açar.
9. **KPI:** `GET /api/operations/summary?hubCode=&date=` (OpsAgent/OpsManager) docs/07 §6 formüllerini parametreli SQL ile hesaplar.
   Hub filtresi: çıkış **veya** varış hub'ı. "İlk denemede teslim" = son 7 tam yerel günde teslim edilip `Failed` denemesi olmayan gönderiler.
   Oran payda 0 ise `null`; pay ve payda her zaman döner.
10. **Audit okuma:** `GET /api/shipments/{id}/audit` yalnız OpsManager; gönderi ve istisna kayıtları, `before`/`limit` keyset sayfalama.

## Sonuçlar
Yan etkiler kaybolmaz ve commit'i bloklamaz; kopya teslim tüketicide zararsızdır. Outbox satır sırası commit sırası değildir;
canlı panel için yalnız ipucudur, yetkili veri her zaman HTTP'den okunur.
