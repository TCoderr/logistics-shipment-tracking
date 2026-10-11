# ADR-015 Operations modülü sınırı: olaylar, okuma view'ı ve çözüm aksiyonları (Dilim 5)

Durum: Kabul edildi (2026-10-10)

## Bağlam
İstisnalar (docs/02 §4.1) gönderi komutlarıyla aynı transaction'da açılmalı (başarısız deneme, konsolidasyon reddi, terminal durum), çözümleri ise
gönderiyi değiştirebilmeli (ek deneme hakkı, göndericiye iade). İki yönlü proje referansı döngü yaratır.

## Kararlar
1. **Proje:** `Lst.Modules.Operations` (istisna domain'i, kuyruk, atama/çözüm). Referans yönü: Operations → Shipments (sözleşmeler), Shipments → Operations **yok** (mimari test).
2. **Olaylar (Shipments → Operations):** Shipments `IShipmentEventSink` olaylarını (`DeliveryAttemptFailed`, `ConsolidationRejected`, `ShipmentReachedTerminal`)
   SaveChanges'tan önce, aynı `DbContext` ve transaction içinde yayınlar. Mediator yok; basit `ShipmentEventPublisher`.
3. **Aksiyonlar (Operations → Shipments):** `IShipmentOperations` (`GrantExtraAttemptAsync`, `StartReturnToSenderAsync`) çağıranın transaction'ında çalışır;
   red sonucu dönerse istisna çözümü de geri alınır (kısmi commit yok).
4. **Okuma (Shipments ← Operations):** Shipments açık istisnaları yalnız `operations.v_open_shipment_exceptions` view'ından (keyless) okur
   (detay `openExceptions`, liste `openExceptionCount`/`hasOpenException`). View migration'da tanımlıdır; tabloyu Operations sahiplenir.
5. **Kilit sırası:** gönderi → istisna (teslimat komutu ve çözüm aynı sırayı izler; deadlock yok). Atama yalnız istisna satırını kilitler.
6. **Tek açık kayıt:** gönderi kilidi altında kontrol + `ux_shipment_exceptions_open_per_type` partial unique index; tekrar `occurrence_count` artırır.
7. **Otomatik çözüm:** terminal durumda (teslim, iade tamamlandı, iptal) operasyonel tipler `AutoResolved`; carrier çakışma tipleri insan kararı bekler.
8. **Yetki:** kuyruk ve komutlar `OperationsStaff`; OpsAgent yalnız kendini atar, çözüm için atanmış olmalı; OpsManager herkese atar/çözer.
   Teslimat denemesi: operasyon personeli veya varış hub'ına atanmış HubOperator; kaynak yetkisi idempotency replay'den önce denetlenir.
9. **Vade:** High 4 saat, Medium 24 saat, Low 72 saat (duvar saati). İş saati tabanlı vade v2.
10. **Deneme idempotency:** `IdempotentCommand` (Platform) ile aynı claim/replay protokolü; kapsam aktör + endpoint, hash gönderi kimliğini içerir (`lst.delivery-attempt.v1`).

## Sonuçlar
Shipments ve Operations ayrı derlenir; tek veritabanı transaction'ı korunur. Operations'ın gönderi verisini sorgu projeksiyonu için okuması (kuyrukta takip no/durum) bilinçli bir monolith kısayoludur.
