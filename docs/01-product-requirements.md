# 01 — Ürün Gereksinimleri

> Durum: Tasarım taslağı (2026-10-09). Bu belge uygulanmış bir sistemi değil, uygulama öncesi kararları anlatır.
> İlgili belgeler: [02 Domain](02-domain-and-state-machines.md) · [03 Mimari](03-architecture-decisions.md) · [05 API](05-api-and-event-contracts.md)

## 1. Ürün özeti

**Logistics & Shipment Tracking System (LST)**, tek bir lojistik işletmesinin birden çok hub/depo üzerinden yürüttüğü gönderi operasyonunu yönetir:

- gönderi kaydı ve paket etiketleri,
- hub taramalarıyla fiziksel hareketin izlenmesi,
- taşıyıcı (carrier) olaylarının güvenli ve tekrar/sıra dışı olaylara dayanıklı alınması,
- teslimat denemeleri ve istisna (exception) kuyruğu,
- alıcıya kişisel veri sızdırmayan sınırlı bir public takip ekranı.

Sistem **sipariş yönetimi, faturalama, rota optimizasyonu veya filo ERP'si değildir.** Ürünün değeri, "bu gönderi şu an nerede, neden gecikti, kim ilgileniyor" sorularına tutarlı ve denetlenebilir yanıt vermesidir.

### Ürün ilkeleri

1. **Güncel durum ile geçmiş ayrıdır.** Her olay geçmişe yazılır; güncel durumu yalnız kurallara uyan olaylar değiştirir.
2. **Hiçbir dış olay sessizce kaybolmaz.** Her olayın sonucu `Applied`, `Duplicate`, `Stale`, `Rejected`, `PendingReview` ya da `Conflict` olarak kaydedilir.
3. **Gecikme ve hata görünürdür.** Gecikmiş ve başarısız gönderiler istisna kuyruğuna düşer, bir sahibi ve çözüm kaydı olur.
4. **Kişisel veri en dar kapsamda gösterilir.** Public ekranda alıcı adı, adresi ve telefonu yoktur; iç ekranlarda rol ve hub kapsamına göre gösterilir.
5. **AI/LLM özelliği yoktur.** Tahmin, chatbot veya otomatik karar üretimi kapsam dışıdır. Teslim tarihi kural tabanlıdır.

## 2. Aktörler

| Aktör | Kimlik | Ana hedef | Erişim sınırı |
|---|---|---|---|
| Operasyon görevlisi (`OpsAgent`) | İç kullanıcı | Gönderi oluşturmak, aramak, atamak, istisnaları çözmek | Tüm hub'ların gönderileri; yönetim ayarları yok |
| Hub görevlisi (`HubOperator`) | İç kullanıcı | Atandığı hub'da paket taramak, dağıtıma çıkarmak, teslimat denemesi kaydetmek | Yalnız atandığı hub(lar)la ilişkili gönderiler ve taramalar |
| Operasyon yöneticisi (`OpsManager`) | İç kullanıcı | Gecikme ve başarısız teslimatları izlemek, audit okumak, istisna yeniden atamak, kullanıcı-hub atamasını yönetmek | Tümü + yönetim işlemleri |
| Alıcı / müşteri | Anonim | Gönderisinin durumunu görmek | Yalnız public takip kodu ile tek gönderinin sınırlı görünümü |
| Taşıyıcı sistemi (carrier) | Makine | Gönderi olaylarını bildirmek | Yalnız kendi carrier koduna bağlı webhook ve kendisine atanmış gönderiler |
| Sistem işleri (worker) | Makine | SLA kontrolü, outbox dağıtımı, retention | İç |

Müşteri hesabı ve gönderen (merchant) portalı ilk sürümde yoktur. Gönderiyi operasyon görevlisi oluşturur ya da gelecekte bir entegrasyon API istemcisi oluşturur.

## 3. Kapsam

### 3.1 İlk sürüm (v1) kapsamı

| Alan | v1'de var | Not |
|---|---|---|
| Gönderi oluşturma | Evet | Gönderen/alıcı adres snapshot'ı, 1–20 paket, servis seviyesi, isteğe bağlı harici referans, kalıcı idempotency |
| Gönderi listesi ve detay | Evet | Arama, filtre, keyset pagination, hub kapsamlı yetki |
| Hub taramaları | Evet | Varış (`Arrival`), çıkış (`Departure`), dağıtıma çıkış (`OutForDelivery`) |
| Teslimat denemesi | Evet | Başarılı/başarısız + neden; en fazla 3 deneme |
| İptal | Evet | Yalnız ilk hub çıkışından önce |
| Göndericiye iade (return-to-sender, RTS) | Evet | Çıkış sonrası durdurma ve başarısız teslimat sonrası iade bu yoldan |
| Taşıyıcı webhook alımı | Evet | Simüle taşıyıcı, HMAC imza, inbox, dedup, sıra dışı olay sınıflandırma |
| Taşıyıcı simülatörü | Evet (yalnız Development) | Senaryo: normal, tekrar, sıra dışı, gecikmiş, çelişkili payload |
| İstisna kuyruğu | Evet | Otomatik açılış, atama, çözüm kaydı |
| Basit SLA | Evet | Söz verilen teslim tarihi + gecikme tespiti (iş günü kuralı) |
| Audit kaydı | Evet | Kim, ne zaman, hangi işlemi yaptı; PII ve ham payload yok |
| Public takip | Evet | Tahmin edilmesi zor kod, rate limit, PII yok |
| Operasyon paneli (UI) | Evet | Liste, detay, istisna kuyruğu, KPI satırı |
| Canlı güncelleme (SignalR) | Sonraki küçük dilim | HTTP snapshot/cursor ile telafi edilir |

### 3.2 Kapsam dışı (v1)

- SaaS çok kiracılı yapı (tenant), abonelik ve faturalama.
- Rota optimizasyonu, kurye atama algoritması, araç kapasite planlaması.
- Filo ERP'si, araç bakımı, sürücü vardiyası.
- Gerçek GPS cihaz entegrasyonu ve canlı araç haritası. Gerçek konum verisi olmadan "canlı araç takibi" gösterilmez.
- Gerçek taşıyıcı API entegrasyonu (yalnız adaptör arayüzü ve simülatör).
- Kapıda ödeme (COD), fiyatlandırma, navlun hesabı.
- Kısmi teslimat (çok paketli gönderinin bir kısmını teslim etmek).
- Teslim sonrası müşteri iadesi lifecycle'ının uygulanması (modelde yeri ayrılır, akış v2).
- Adres doğrulama/geocoding servisleri, SMS/e-posta sağlayıcıları (bildirimler v1'de yalnız log kanalına simüle edilir).
- Gelişmiş iş takvimi (hub bazlı vardiya, bölgesel tatil), routing.
- AI/LLM tabanlı her türlü özellik.

## 4. Temel kavramlar ve kararlar

### 4.1 Shipment ve Package ayrımı — gerekli

- **Shipment (gönderi):** Ticari ve operasyonel birim. Bir gönderen, bir alıcı, bir servis seviyesi, bir durum makinesi, bir tracking numarası.
- **Package (paket):** Fiziksel birim. Her paketin kendi etiketi (`LS4829105735-01`) vardır; hub taramaları paket etiketiyle yapılır.

Ayrım gereklidir çünkü hub'da taranan şey fiziksel kolidir, müşteriye söz verilen şey ise gönderidir. Ayrım olmazsa "3 koliden biri eksik" durumu görünmez.

**Çok paketli gönderi kuralı (v1):**

1. Gönderinin durumu tek bir durum makinesiyle yönetilir; paketlerin ayrı durum makinesi yoktur. Paket yalnız konum bilgisi taşır (`CurrentHubId`, son tarama tipi ve zamanı).
2. Bir paketin bir hub'a varış taraması, gönderiyi o hub'da `AtHub` durumuna getirir.
3. **Konsolidasyon kuralı:** `Departure` ve `OutForDelivery` taramaları, gönderinin **tüm paketleri** aynı hub'da tarandıysa kabul edilir. Aksi halde tarama `Rejected` (`PackagesNotConsolidated`) olur ve `PackageSplit` istisnası açılır.
4. **Teslimat hep-ya-hiç:** Gönderi ancak tüm paketleri teslim edildiğinde `Delivered` olur. Teslimatta paket eksikse deneme `Failed` + `PackageMissing` olarak kaydedilir. Kısmi teslimat v1'de yoktur.

### 4.2 Sipariş referansı

`ExternalReference` isteğe bağlı, serbest metin (en fazla 64 karakter) bir harici referanstır (ör. e-ticaret sipariş no). Sistem siparişin otoritesi değildir:

- Benzersiz değildir; bir siparişe birden çok gönderi açılabilir.
- Sipariş durumu, tutarı, kalemleri tutulmaz.
- Aramada kullanılabilir (indeksli). Tekrar gönderi oluşturmayı engellemek için kullanılmaz; bu iş `Idempotency-Key` ile yapılır.

### 4.3 Tracking numarası ve public takip kodu

| Kimlik | Biçim | Gizli mi? | Kullanım |
|---|---|---|---|
| `TrackingNumber` | `LS` + 9 rastgele rakam + Luhn kontrol hanesi (ör. `LS4829105735`) | Hayır, etikette basılı | İç arama, telefon desteği, carrier eşlemesi |
| Paket etiketi (`LabelCode`) | `{TrackingNumber}-{sıra:00}` | Hayır | Hub taraması |
| `PublicTrackingCode` | 128 bit rastgele, Crockford Base32, 26 karakter | Evet (bearer yetki) | Alıcıya gönderilen link: `/t/{code}` |

Public ekran yalnız `PublicTrackingCode` ile açılır; tracking numarası tek başına public erişim vermez. Bu, tracking numarasının etiketten okunarak enumerate edilmesini engeller. Kod operasyon yöneticisi tarafından yenilenebilir (eski kod geçersizleşir).

### 4.4 Servis seviyesi ve söz verilen teslim tarihi

- `Standard`: kabul tarihinden itibaren 2 iş günü.
- `Express`: 1 iş günü.
- Kabul kesim saati 15:00 (Europe/Istanbul). Sonrasında oluşturulan gönderi ertesi iş günü kabul edilmiş sayılır.
- İş günü: Pazartesi–Cumartesi. Resmî tatiller uygulama konfigürasyonundaki tarih listesinden okunur.
- `PromisedDeliveryDate` gönderi oluşturulurken hesaplanır ve saklanır; kural sonradan değişirse geçmiş gönderiler etkilenmez.

> **Varsayım:** Cumartesi teslimatı ve 15:00 kesim saati Türkiye kargo pratiğine göre seçilmiştir; iş birimi onayı gerekir.

## 5. Use-case'ler

Her use-case için ana akış, hata akışları ve yetki kısa tutulmuştur. API karşılıkları [05](05-api-and-event-contracts.md)'tedir.

### UC-01 Gönderi oluştur (OpsAgent, OpsManager)

**Ana akış:** Görevli gönderen ve alıcı bilgilerini, paketleri (ağırlık, boyut), servis seviyesini, çıkış ve varış hub'ını girer → sistem tracking numarası, paket etiketleri, public takip kodu ve söz verilen teslim tarihini üretir → gönderi `Created` durumunda kaydedilir → audit kaydı yazılır.

**Hata akışları:**
- Zorunlu alan eksik / geçersiz telefon, posta kodu, paket ölçüsü → `400` ProblemDetails, alan bazlı hatalar.
- Pasif veya bilinmeyen hub → `422 unknown-hub`.
- `Idempotency-Key` yok → `400 idempotency-key-required`.
- Aynı anahtar, aynı içerik, önceki istek tamamlanmış → orijinal `201` yanıtı tekrar döner (`Idempotency-Replayed: true`). Yeni gönderi oluşmaz.
- Aynı anahtar, farklı içerik → `422 idempotency-key-reused`.
- Aynı anahtarla eşzamanlı istek ve kilit zaman aşımı → `409 idempotency-request-in-progress`; istemci aynı anahtarla tekrar dener.

### UC-02 Gönderi ara ve listele (OpsAgent, OpsManager, HubOperator)

Tracking no, paket etiketi, harici referans, alıcı telefonunun son 4 hanesi ile arama; durum, hub, carrier, gecikmiş, açık istisna, oluşturma tarih aralığı filtreleri. HubOperator yalnız kendi hub kapsamındaki gönderileri görür (kapsam tanımı [06](06-security-and-failure-behavior.md)).

### UC-03 Gönderi detayı (tüm iç roller, kapsam dahilinde)

Durum özeti, söz verilen tarih ve gecikme, paketler ve konumları, olay zaman çizelgesi (geç gelen ve uygulanmayan olaylar işaretli), teslimat denemeleri, açık istisnalar ve **o kullanıcının o anda yapabileceği** işlemler. Kapsam dışı gönderi için `404` döner (varlık bilgisi sızdırılmaz).

### UC-04 Hub taraması (HubOperator, kendi hub'ında)

Görevli paket etiketini okutur ve tarama tipini seçer → sistem durum makinesine göre değerlendirir → sonuç anında gösterilir: `Applied`, `Duplicate` (aynı tarama zaten var), `Rejected` (neden kodu ile). Örnek ret: başka hub'ın kullanıcısı, konsolidasyon eksik, terminal durum, varış hub'ı değil.

### UC-05 Teslimat denemesi kaydet (HubOperator — varış hub'ı; OpsAgent)

`OutForDelivery` gönderi için sonuç girilir. Başarılı → `Delivered` (terminal). Başarısız → `DeliveryFailed`, neden zorunlu, `DeliveryFailed` istisnası açılır/güncellenir. 3. başarısız denemede `MaxAttemptsReached` istisnası açılır ve yöneticinin ek deneme izni veya iade kararı olmadan yeni dağıtıma çıkış reddedilir.

### UC-06 İptal (OpsAgent, OpsManager)

Yalnız `Created` veya ilk çıkış taraması yapılmamış `AtHub` gönderilerde, taşıyıcıya devredilmemişse. Neden zorunlu. Çıkış sonrası iptal isteği **iptal değil, göndericiye iade (UC-07)** olarak işlenir; böylece fiziksel olarak yolda olan bir koli "iptal edildi" görünüp kaybolmaz.

### UC-07 Göndericiye iade başlat (OpsAgent, OpsManager)

`InTransit`, çıkış yapmış `AtHub` veya `DeliveryFailed` gönderiler için. Gönderi `ReturningToSender` olur, hub taramaları konum güncellemeye devam eder, göndericiye teslimde `ReturnedToSender` (terminal) olur.

### UC-08 Taşıyıcı olayı al (Carrier)

Carrier imzalı webhook gönderir → imza, zaman damgası, boyut ve şema doğrulanır → olay inbox'a kalıcı yazılır ve `202` döner → worker olayı değerlendirir ve sonucunu kaydeder. Tekrar, sıra dışı, gecikmiş, çelişkili olaylar [02 §5](02-domain-and-state-machines.md#5-olay-alımı-ve-sıralama-kuralları)'teki kurallarla sınıflandırılır.

### UC-09 İstisna kuyruğunu yönet (OpsAgent, OpsManager)

Kuyruk: tip, önem, yaş, hub, atanan kişi, SLA durumu. Görevli istisnayı üzerine alır veya yönetici atar → inceleme → çözüm kodu + not ile kapatır. Bazı çözüm kodları bir gönderi işlemi tetikler (ör. `ReturnToSenderInitiated` → UC-07, `ReattemptApproved` → +1 deneme hakkı).

### UC-10 Operasyon özeti (OpsManager, OpsAgent)

Tanımlı formüllerle KPI satırı: bugün teslimi beklenen, gecikmiş açık gönderi, zamanında teslim oranı, ilk denemede teslim oranı, açık istisna sayısı ve en eski yaşı. Formüller [07 §6](07-ui-design.md#6-kpi-tanımları)'dadır.

### UC-11 Public takip (Alıcı)

Alıcı linki açar → durum, söz verilen tarih, şehir düzeyinde olay zaman çizelgesi ve paket sayısı görünür. Alıcı adı, adresi, telefonu, gönderen kişisel bilgisi, iç notlar, istisna detayları, hub iç kodları gösterilmez. Geçersiz kod ile var olmayan kod aynı `404` yanıtını alır. IP başına rate limit uygulanır.

### UC-12 Audit inceleme (OpsManager)

Bir gönderi veya kullanıcı için kim/ne zaman/hangi işlem/sonuç. v1'de gönderi detayındaki "İşlem geçmişi" sekmesi ve API ile; ayrı audit arama ekranı v2.

## 6. Fonksiyonel olmayan gereksinimler

| Konu | Gereksinim |
|---|---|
| Tutarlılık | Durum değişikliği, zaman çizelgesi kaydı, audit ve outbox mesajı aynı DB transaction'ında. |
| Dayanıklılık | Webhook `202` yalnız inbox kaydı commit edildikten sonra döner. Worker çökmesi lease süresi sonunda telafi edilir. |
| Eşzamanlılık | Gönderi satırında optimistic concurrency; otomatik akışlar yeniden değerlendirir, kullanıcı işlemleri `If-Match` ile çakışmayı görür. |
| Zaman | Tüm zamanlar UTC `timestamptz`; iş kuralları ve gösterim Europe/Istanbul. Olayın gerçekleşme ve alınma zamanı ayrı saklanır. |
| Gizlilik | PII yalnız `shipment_parties` tablosunda; log ve audit'e PII yazılmaz; terminal durumdan 180 gün sonra maskelenir (varsayım, hukuk onayı gerekir). |
| Gözlemlenebilirlik | Yapılandırılmış log + correlation id, health check (`/health/live`, `/health/ready`), worker gecikme metrikleri (inbox/outbox yaşı). |
| Performans | Yük testi yapılmadan kapasite iddiası yok. Ölçüm planı [09 §8](09-test-and-verification-plan.md#8-gelecekteki-benchmark-planı)'de. |
| Erişilebilirlik | WCAG 2.2 AA hedefi; klavye ile tam kullanım; durum renk dışında ikon+metinle de kodlanır. |
| Dil | Arayüz Türkçe; kod, API alanları ve enum değerleri İngilizce. |

## 7. Açık varsayımlar ve karar bekleyenler

| # | Varsayım / açık konu | Seçilen geçici karar | Kim onaylamalı |
|---|---|---|---|
| A1 | İş günü Pzt–Cmt, kesim 15:00 | Konfigürasyonla değişebilir | İş birimi |
| A2 | PII saklama süresi 180 gün | Konfigürasyon; maskeleme işi v1'de | Hukuk / KVKK sorumlusu |
| A3 | Ham webhook payload 30 gün | Yalnız DB erişimi, UI'da gösterim yok | Güvenlik |
| A4 | Maksimum 3 teslimat denemesi | Yönetici +1 izin verebilir | Operasyon |
| A5 | Gönderi başına en fazla 20 paket | Check constraint | Operasyon |
| A6 | Varış hub'ı oluşturmada elle seçilir | Şehir→hub eşlemesi v2 | Operasyon |
| A7 | Kısmi teslimat yok | Eksik paket = başarısız deneme | Operasyon |
| A8 | Public ekranda gönderen adı yok | Sonradan "kurumsal gönderen adı" eklenebilir | Ürün |
| A9 | Kimlik doğrulama yerel ASP.NET Core Identity | Kurum SSO gelirse OIDC'ye geçiş | BT |
