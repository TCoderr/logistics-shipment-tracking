# ADR-014 Aggregate kilidi, tarama kararları ve iptal (Dilim 4)

Durum: Kabul edildi (2026-10-10)

## Bağlam
docs/02 §6 ilk taslağı iş tekrarı yarışını `xmin` değişimiyle çözmeyi öneriyordu. `xmin` yalnız satır güncellenince değişir;
paket-yalnız, stale veya yalnız olay ekleyen işlemler gönderi satırına dokunmazsa iki eşzamanlı istek aynı olayı iki kez ekleyebilir.

## Kararlar
1. **Pessimistic aggregate kilidi.** Durum değiştiren her gönderi komutu (tarama, iptal; sonraki dilimlerde deneme, iade, carrier olayı)
   READ COMMITTED transaction içinde gönderi satırını `SELECT … FOR UPDATE` ile kilitler (`lock_timeout = 5s`, aşımda `503 shipment-busy`).
   Teknik tekrar (`hub + clientScanId`), iş tekrarı (aynı paket + tip + hub, ±120 sn) kontrolü, karar ve INSERT bu kilit altındadır.
2. **Touch.** Uygulanan veya stale kaydedilen her tarama gönderinin `updated_at`'ini günceller; böylece ETag (xmin) zaman çizelgesi değişikliğini yansıtır.
3. **Saf karar fonksiyonu.** `ScanPolicy.Decide` DB/saat erişimi olmadan: saat sapması → iş tekrarı → paket düzeyi stale → terminal koruma →
   geçiş/ön koşullar → (yalnız durum değiştirecekse) gönderi düzeyi stale. Paket-yalnız konum güncellemesi gönderi düzeyi zaman kuralına takılmaz
   (aksi halde aynı hub'a daha erken varan ikinci koli yanlışlıkla stale olurdu).
4. **Konsolidasyon önce.** Çıkış ve dağıtıma çıkış ön koşulu, taranan paketin konumu değişmeden önceki durum üzerinden değerlendirilir.
5. **Rejected yazmaz.** Reddedilen taramada paket/gönderi/olay yazılmaz; yalnız `HubScanRejected` audit kaydı (reason_code) tutulur.
6. **İş anahtarı paket içerir.** `HubScan:{hub}:{eventType}:{packageId}`; çok paketli taramalar birbirinin tekrarı sayılmaz.
7. **İptal.** `If-Match` zorunlu (yoksa 428); kilit altında güncel ETag ile karşılaştırılır (eskiyse 412 + güncel ETag). Yalnız `Created` veya
   çıkış yapmamış `AtHub`; diğerleri `409 shipment-not-cancellable` + `returnToSenderAllowed`. Neden metni gönderide saklanır, audit'e yalnız
   `reasonProvided` yazılır (serbest metin PII içerebilir).
8. **Retry.** Taramada eşzamanlılık kilit beklemesiyle çözülür (yeniden deneme gerekmez); operatör komutunda eski ETag her zaman 412'dir.
9. **allowedActions.** Durum + güncel DB rolünden türetilir (`ShipmentActions.For`); sunucu her komutu yeniden doğrular.
10. **Too-old.** 7 günden eski kullanıcı taraması `422 invalid-occurred-at` (`too-old`) ile reddedilir; carrier akışında docs/02'deki `PendingReview` geçerlidir.

## Sonuç
Eşzamanlı iki koli varışı tek durum geçişi üretir; aynı clientScanId/aynı iş olayı tek etki bırakır; çakışan iki iptalden biri 412 alır (integration testleri).
