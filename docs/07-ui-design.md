# 07 — Arayüz Tasarımı

> İlgili: [01 Use-case'ler](01-product-requirements.md#5-use-caseler) · [05 API](05-api-and-event-contracts.md) · Statik prototip: [`../design/operations-preview.html`](../design/operations-preview.html)
> Prototip **Demo** verisiyle çalışan, backend'e bağlı olmayan, tek dosyalık bir tasarım taslağıdır. Ürün frontend'i değildir.

## 1. Kullanılan skill'ler ve gerekçesi

Ortamda incelenen skill'ler (2026-10-09):

| Skill | Kullanıldı mı | Neden / nasıl |
|---|---|---|
| `artifact-design` | **Evet, dar kapsamda** | Renkleri token olarak tanımlama, açık/koyu tema için token düzeyinde yeniden tanım, `tabular-nums`, yoğun UI'da "özet önce, ayrıntı sonra", durumun renk dışında biçimle (etiket, ikon) kodlanması, kart stilini yalnız rolü olan öğeye verme, klişe AI estetiğinden (mor gradient, cam kart, her yerde yuvarlak kart) kaçınma ilkeleri uygulandı. Skill'in "editorial" (landing page, büyük hero, tipografik gösteri) bölümü **bilerek uygulanmadı**: bu bir operasyon panelidir. |
| `dataviz` | **Evet, yalnız biçim seçimi ve durum renkleri için** | "Biçimi önce seç" adımı: KPI'lar tek değerli olduğu için grafik değil kompakt sayı satırı; >7 sınıflı veri (başarısızlık nedenleri) için tablo. Durum renkleri (iyi/uyarı/ciddi/kritik) ayrılmış ve **daima ikon + metinle** kullanılıyor. Dual-axis, dekoratif grafik ve büyük "hero number" kullanılmadı. Kategorik grafik olmadığı için palet doğrulama scripti çalıştırılmadı. |
| `artifact-diagramming` | Hayır | Mimari diyagramlar Mermaid ile dokümanlarda; ek görsel gerekmedi. |
| Frontend design / erişilebilirlik / dashboard'a özel skill | **Ortamda yok** | Yokmuş gibi davranılmadı. Erişilebilirlik için WCAG 2.2 AA ve WAI-ARIA Authoring Practices (tablist, tablo sıralama) kendi bilgimle uygulandı. Harici skill indirilmedi (gerek görülmedi). |

Öncelik sırası: kullanıcının talimatları → bu projenin gereksinimleri → skill önerileri.

## 2. Kullanıcı görevleri ve bilgi hiyerarşisi

| Kullanıcı | En sık görev | İlk bakışta görmesi gereken | İkincil |
|---|---|---|---|
| OpsAgent | "Bu gönderi nerede, neden takıldı?" (telefon/e-posta ile gelen soru) | Arama kutusu; durum, söz verilen tarih, gecikme, açık istisna | Paketler, timeline, denemeler |
| OpsAgent | İstisna kuyruğunu eritmek | Vadesi geçmiş ve yüksek önemli istisnalar, sahibi olmayanlar | Gönderi bağlamı, çözüm seçenekleri |
| HubOperator | Taramak ve sonucu anında görmek | Son tarama sonucu (Uygulandı / Tekrar / Reddedildi + neden), konsolidasyon durumu (2/3 koli) | Hub'daki bekleyen gönderiler |
| OpsManager | Gün içinde riskleri görmek | Gecikmiş açık gönderi sayısı, vadesi geçen istisnalar, başarısız deneme nedenleri | Zamanında teslim oranı, audit |
| Alıcı | "Kargom ne zaman gelir?" | Durum, söz verilen tarih, son olay | Şehir düzeyinde geçmiş |

Bilgi hiyerarşisi kuralı: **istisna > durum > zaman > konum > kimlik.** Bir satırda göze ilk çarpan şey, ilgilenilmesi gereken bir sorun varsa o sorun olmalıdır; sorun yoksa durum.

## 3. Görsel dil

- **Yoğunluk:** Tablo satır yüksekliği 36 px (kompakt), 13–14 px metin. Operasyon ekranı 1280–1920 px masaüstü için tasarlanır; 1024 px'te yatay kaydırma tablonun kendi kabında.
- **Renk:** Nötr, hafif soğuk griler; tek vurgu rengi (koyu mavi) yalnız etkileşim (bağlantı, birincil buton, seçili sekme) için. Durum renkleri vurgu renginden ayrıdır ve yalnız durum/önem için kullanılır.
- **Durum etiketleri:** Her durum = ikon (şekil) + Türkçe metin + renk. Renk körlüğünde ve gri tonlamada şekil ve metin yeterli.

| Durum | Etiket | Ton | Şekil |
|---|---|---|---|
| `Created` | Kaydedildi | Nötr | Boş daire |
| `AtHub` | Hub'da | Bilgi | Kare |
| `InTransit` | Yolda | Bilgi | Sağ ok |
| `OutForDelivery` | Dağıtımda | Bilgi (koyu) | Çift ok |
| `DeliveryFailed` | Teslim edilemedi | Ciddi | Ünlem üçgeni |
| `Delivered` | Teslim edildi | İyi | Onay |
| `ReturningToSender` | İadede | Uyarı | Sol ok |
| `ReturnedToSender` | İade edildi | Nötr | Sol ok + çizgi |
| `Cancelled` | İptal | Nötr | Çarpı |

Gecikme ayrı bir etiket: "1 gün gecikti" (kritik ton, saat ikonu). İstisna sayısı ayrı rozet.

- **Tipografi:** Sistem fontları (Segoe UI / system-ui; tracking no ve etiketler için `ui-monospace`/Cascadia Mono/Consolas). Gerekçe: operasyon ekranında hızlı yükleme, çevrimdışı prototip, Windows ağırlıklı kullanıcı ortamı; tracking numaraları monospace ile hane hane okunur.
- **Hareket:** Yalnız yükleme göstergesi ve yeni gelen satırın 1 sn'lik arka plan vurgusu (canlı güncelleme dilimi). `prefers-reduced-motion` ile kapanır.
- **Kullanılmayanlar:** Gradient, glow, cam efekti, dekoratif illüstrasyon, büyük pazarlama başlığı, KPI'larda büyük süs sayıları, pasta grafik.

## 4. Ekranlar

### 4.1 Gönderi listesi (`/shipments`)

```
┌ LST Operasyon ─ [Gönderiler] [İstisnalar 12] [Özet] ────────── Demo · M. Arslan (OpsAgent) ┐
│ Bugün teslim beklenen 184 · Gecikmiş açık 9 · Vadesi geçen istisna 3 · Son güncelleme 14:02 │
├──────────────────────────────────────────────────────────────────────────────────────────────┤
│ [/ Ara: tracking no, koli etiketi, sipariş ref, tel son 4      ]  [+ Gönderi oluştur]         │
│ Durum [Tümü ▾]  Hub [Tümü ▾]  Carrier [Tümü ▾]  [☐ Gecikmiş] [☐ Açık istisna]  Oluşturma [7g▾]│
│ Aktif filtreler: Durum: Teslim edilemedi ×   Hub: ANK-02 ×          Filtreleri temizle        │
├────────────┬───────────────────┬────────┬───────────┬────────┬─────────┬────────┬────────────┤
│ Tracking   │ Durum             │ Söz    │ Gecikme   │ Rota   │ Şu an   │ İstisna│ Son değişim│
│ LS48291…35 │ ▲ Teslim edilemedi│ 12 Eki │ —         │IST→ANK │ —       │ ● 1    │ 10 Eki 12:47│
│ LS50177…12 │ ⇉ Dağıtımda       │ 09 Eki │ ◷ 1 gün   │IZM→ANK │ ANK-02  │ ● 1    │ 10 Eki 08:05│
│ …                                                                                             │
│                                   [Daha fazla yükle]                                          │
└──────────────────────────────────────────────────────────────────────────────────────────────┘
```

- **Arama:** `/` kısayolu odaklar; tracking no veya etiket tam eşleşirse doğrudan detaya gider.
- **Filtreler** URL query string'inde tutulur (paylaşılabilir, geri tuşu çalışır).
- **Sıralama:** yalnız indeksli sütunlarda (`Söz`, `Son değişim`, varsayılan `Oluşturma`). Başlıkta `aria-sort`.
- **Satır:** tamamı tıklanabilir değil; tracking numarası bağlantıdır (klavye ve orta tık ile yeni sekme doğal çalışır).
- **Sayılar:** "Gecikmiş açık 9" gibi üst şerit değerleri tıklanınca ilgili filtreyi uygular.

### 4.2 Gönderi detayı (`/shipments/{id}`)

```
┌ ← Gönderiler   LS4829105735   ▲ Teslim edilemedi   Standard   Söz: 12 Eki (Pzt)   Sipariş: ORD-2026-100245 ┐
│ Açık istisna: Teslim edilemedi · Orta · vade 12 Eki 12:47 · sahibi yok   [Üzerime al]                         │
├───────────────────────────────────────────────┬──────────────────────────────────────────────────────────────┤
│ Zaman çizelgesi                    [Tümü|Uyg.]│ İzinli işlemler                                              │
│ 10 Eki 12:47  ▲ Teslim edilemedi  ANK-02      │ [Göndericiye iade başlat]  (iptal: çıkış yapıldığı için yok) │
│               Deneme 1/3 · Alıcı adreste yok  │                                                              │
│ 10 Eki 10:05  ⇉ Dağıtıma çıktı    ANK-02      │ Paketler (2)                                                 │
│ 10 Eki 08:15  ■ Hub'a ulaştı ANK-02 · 2/2 koli│ LS4829105735-01  2,4 kg  40×30×20  son: Dağıtım · ANK-02     │
│ 09 Eki 21:40  → Hub'dan çıktı IST-01 · 2/2    │ LS4829105735-02  0,9 kg  30×20×10  son: Dağıtım · ANK-02     │
│ 09 Eki 19:20  ■ Hub'a ulaştı IST-01 (koli 01) │                                                              │
│               Geç alındı 10 Eki 09:20 ·       │ Alıcı   Ece Yılmaz · +90532*****67                           │
│               Uygulanmadı (eski olay)         │         Kızılay Mh. 5. Sk. No: 3 D: 7, Çankaya / Ankara      │
│ 09 Eki 19:02  ■ Hub'a ulaştı IST-01 · 2/2 koli│                                                              │
│ 09 Eki 14:20  ○ Kaydedildi        Operatör    │ Gönderen Kaya Tekstil Ltd. · İstanbul                         │
│                                               │ Denemeler  1/3  ·  İşlem geçmişi (audit) →                   │
└───────────────────────────────────────────────┴──────────────────────────────────────────────────────────────┘
```

- Üst şerit: durum, söz verilen tarih, gecikme, açık istisna — **tek bakışta karar.**
- Timeline olay zamanı (`occurredAt`) sırasıyla. Aynı hub'da aynı tipteki paket taramaları tek satırda "2/2 koli" olarak gruplanır, satır açılınca koli bazında görünür; `receivedAt` farkı 10 dk'yı aşan olaylarda "alındı: 12:05" ikincil metni. `Stale` olaylar soluk değil, kesik kenarlı ve "Geç alındı · Uygulanmadı" etiketiyle (okunabilirlik korunur). `PendingReview` olaylar "İnceleme bekliyor" etiketi ve istisnaya bağlantı.
- **İzinli işlemler** yalnız API'nin `allowedActions` listesinden. Olmayan işlem için kısa açıklama ("İptal yok: gönderi hub'dan çıktı"), çünkü kullanıcı "neden buton yok" diye sormamalı.
- Onay gerektiren işlemler (iptal, iade) sayfa içi onay paneli: neden alanı + "Göndericiye iadeyi başlat" butonu. Tarayıcı `confirm()` kullanılmaz.
- `412` çakışmasında: "Bu gönderi siz bakarken güncellendi (Dağıtıma çıktı, 14:03). Güncel durumu yükledik; işlemi tekrar gözden geçirin."

### 4.3 İstisna kuyruğu (`/exceptions`)

```
┌ İstisnalar ─ [Benim (4)] [Sahipsiz (7)] [Tümü açık (23)] [Çözülen (bugün 18)] ─────────────────────┐
│ Tip [Tümü ▾]  Önem [Tümü ▾]  Hub [Tümü ▾]  [☑ Vadesi geçenler önce]                                │
├──────┬───────────────────────┬─────────────┬──────────────┬──────────┬───────────┬────────────────┤
│ Önem │ Tip                   │ Gönderi     │ Durum        │ Hub      │ Vade      │ Sahibi         │
│ ◆ Yük│ Deneme hakkı bitti ×3 │ LS50177…12  │ Teslim edil. │ ANK-02   │ ◷ 2 sa geçti│ —  [Üzerime al]│
│ ◆ Yük│ Carrier olay çakışması│ LS39011…08  │ Yolda        │ —        │ 1 sa 40 dk│ S. Demir       │
│ ◇ Orta│ Gecikme              │ LS22870…44  │ Hub'da       │ IZM-01   │ yarın 09:00│ —             │
└──────┴───────────────────────┴─────────────┴──────────────┴──────────┴───────────┴────────────────┘
  Seçili: LS50177…12 · Deneme hakkı bitti
  Son 3 deneme: Alıcı yok (09 Eki) · Alıcı yok (10 Eki) · Adres bulunamadı (12 Eki)
  Çözüm [Ek deneme onayla ▾]  Not [ ............................................ ]  [Çözümü kaydet]
```

- Varsayılan sıra: vadesi geçmiş → önem → vade. Sorunun yaşı her satırda görünür.
- Seçili istisna paneli satırın altında açılır; liste bağlamı kaybolmaz. Klavye: `↑/↓` satır, `Enter` aç, `Esc` kapat.
- Çözüm kodu tipine göre filtrelenir (ör. `MaxAttemptsReached` için yalnız `ReattemptApproved`, `ReturnToSenderInitiated`).

### 4.4 Public takip (`/t/{code}`, mobil öncelikli)

```
┌─────────────────────────────┐
│ Gönderi takibi              │
│ LS48******35 · 2 koli       │
│                             │
│ ⇉ Dağıtımda                 │
│ Tahmini teslim: 12 Ekim Pzt │
│ Son güncelleme: 09:58       │
│                             │
│ ● 12 Eki 09:58 Dağıtıma     │
│   çıktı · Ankara            │
│ ● 10 Eki 12:47 Teslim       │
│   edilemedi, yeniden        │
│   denenecek · Ankara        │
│ ● 10 Eki 10:05 Dağıtıma     │
│   çıktı · Ankara            │
│ ● 10 Eki 08:15 Aktarma      │
│   merkezine ulaştı · Ankara │
│ ● 09 Eki 21:40 Aktarma      │
│   merkezinden çıktı ·       │
│   İstanbul                  │
│ ○ 09 Eki 14:20 Kayıt        │
│   oluşturuldu               │
│                             │
│ Ad, adres ve telefon        │
│ bilgileri bu sayfada        │
│ gösterilmez.                │
└─────────────────────────────┘
```

- 360 px genişlikte tek sütun, 16 px kenar boşluğu, 44 px dokunma hedefleri.
- Harita yok (bkz. §8). "Kurye şu an X sokakta" gibi gerçek veri olmayan bilgi yok.
- Teslim edilemedi durumunda genel metin: "Teslim edilemedi. Yeniden denenecek." Neden kodu gösterilmez.

## 5. Durumlar (her ekran için)

| Durum | Davranış |
|---|---|
| Yükleniyor | Tablo iskeleti (satır yerleri), ilk 300 ms'de gösterilmez (titreme olmasın); başlık ve filtreler hemen etkin |
| Boş (filtre sonucu) | "Bu filtrelerle gönderi yok." + aktif filtre listesi + "Filtreleri temizle" |
| Boş (ilk kullanım) | "Henüz gönderi yok. İlk gönderiyi oluşturun." + birincil buton (yalnız yetkili rolde) |
| Hata (ağ/500) | Satır içi uyarı: "Gönderiler yüklenemedi. Bağlantınızı kontrol edip yeniden deneyin." + "Yeniden dene" + `traceId` (destek için kopyalanabilir) |
| Yetki yok (403) | "Bu işlem için yetkiniz yok. Hub ataması için operasyon yöneticinize başvurun." Yetkisiz işlemin butonu hiç gösterilmez; doğrudan URL ile gelinirse bu mesaj görünür |
| Bulunamadı (404) | "Gönderi bulunamadı veya erişiminiz yok." (kapsam kuralı nedeniyle ikisi ayrıştırılmaz) |
| Bayat veri | Canlı bağlantı yoksa üst şeritte "Canlı güncelleme kapalı · son yenileme 14:02 · [Yenile]". 5 dk'dan eski veri sarı uyarı tonunda |
| Çakışma (412) | §4.2'deki metin; güncel veri otomatik yüklenir, kullanıcının yazdığı not korunur |
| Tarama sonucu | Büyük, sesli okunur (`aria-live="assertive"`) sonuç kutusu: "Uygulandı · Hub'da · 2/3 koli" veya "Reddedildi · Kolilerin hepsi hub'da değil (2/3)" |

## 6. KPI tanımları

Tüm KPI'lar `GET /api/operations/summary?hubCode=&date=` ile sunucuda hesaplanır; Europe/Istanbul takvimi; "as of" zamanı yanında gösterilir. Uydurma veya yuvarlanmış "büyük" sayı yoktur; payda küçükse (<20) oran yerine "7 / 9" gösterilir.

| KPI | Formül | Neden |
|---|---|---|
| Bugün teslim beklenen | `COUNT(*) WHERE promised_delivery_date = today AND kind='Forward' AND status <> 'Cancelled'` + bunlardan `Delivered` olan sayısı ("184 · 121 teslim") | Günün iş yükü |
| Gecikmiş açık | `COUNT(*) WHERE status NOT IN ('Delivered','ReturnedToSender','Cancelled','ReturningToSender') AND promised_delivery_date < today` (= `IsDelayed`) | Doğrudan aksiyon listesi; tıklanınca filtre |
| Zamanında teslim oranı (7 gün) | `COUNT(delivered_at::date(local) <= promised_delivery_date) / COUNT(*)` — `delivered_at` son 7 tam günde olanlar | SLA performansı; pay/payda gösterilir |
| İlk denemede teslim oranı (7 gün) | Teslim edilenlerden `attempt_number = 1` ile teslim edilenler / teslim edilenler | Başarısız deneme maliyetinin göstergesi |
| Açık istisna | `COUNT(*) WHERE status IN ('Open','Assigned')`, ayrıca vadesi geçen sayısı ve en eski açık istisnanın yaşı | Kuyruk sağlığı |
| Başarısız deneme nedenleri (bugün) | `delivery_attempts WHERE outcome='Failed' AND occurred_at local date = today GROUP BY failure_reason` → **tablo** | Grafik değil: 7 kategori, kesin sayı önemli |
| Carrier olay sonuçları (24 sa) | `carrier_event_receipts GROUP BY outcome` | Entegrasyon sağlığı (Duplicate/Stale oranı) |

## 7. Erişilebilirlik

- WCAG 2.2 AA: metin kontrastı ≥ 4.5:1, büyük metin ve UI kenarları ≥ 3:1 (her iki temada).
- Tam klavye: sekme sırası görsel sırayla aynı; görünür odak halkası (2 px, vurgu rengi + dış boşluk); `/` arama, `g s` gönderiler, `g e` istisnalar (yalnız odak bir metin alanında değilken).
- Sekmeler WAI-ARIA `tablist` deseni (ok tuşları ile gezinme). Tablolar gerçek `<table>`, başlıklar `<th scope="col">`, sıralanabilir başlıklar buton + `aria-sort`.
- Durum asla yalnız renkle değil: ikon şekli + metin. İkonlar `aria-hidden`, metin okunur.
- Canlı bölgeler: tarama sonucu `assertive`, liste güncelleme bildirimi `polite`; her satır değişikliğinde okuma yapılmaz (gürültü).
- Zaman bilgisi `<time datetime="...">`; göreli zaman ("2 sa geçti") yanında mutlak zaman `title` ile değil görünür ikincil metinle veya erişilebilir adla.
- Hareket azaltma: `prefers-reduced-motion` ile vurgu animasyonu kapanır.
- Yakınlaştırma %200'de yatay kaydırma yalnız tablo kabında.

## 8. Harita kararı

- **v1'de harita yok.** Elimizde gerçek araç/kurye konumu yok; hub şehirleri dışında coğrafi veri yok. Hub'lar arası düz çizgi çizen bir harita bilgi eklemez, yanlış "canlı takip" izlenimi verir.
- Gelecekte eklenirse ayrı karar: veri kaynağı (gerçek GPS mi, yalnız hub konumları mı), tile sağlayıcı maliyeti ve kullanım şartları (OpenStreetMap tile politikası üretim trafiğine uygun değil; ücretli sağlayıcı veya kendi tile sunucusu), KVKK (kurye konumu kişisel veridir).

## 9. UI teknolojisi (özet)

React + TypeScript + Vite, aynı origin; gerekçe [03 ADR-11](03-architecture-decisions.md#adr-11-ui-stack-react--typescript-vite-aynı-origin). Bileşen kütüphanesi kullanılmaz; ihtiyaç: tablo, etiket, filtre çubuğu, sekme, onay paneli, timeline. Tasarım token'ları prototipteki CSS değişkenleriyle aynı adları taşır.

## 10. Prototip hakkında

`design/operations-preview.html`:

- Tek dosya, harici istek yok (font/CDN yok), JavaScript yalnız sekme ve tablo/satır seçimi için.
- Tüm veriler **sentetik ve "Demo" olarak işaretli**; kişi adları uydurmadır.
- Backend'e bağlanmaz; butonlar işlem yapmaz, yalnız durumu gösterir (ör. onay paneli açılır).
- Gösterdikleri: liste + filtre çubuğu, detay + timeline (geç alınmış olay dahil), istisna kuyruğu, mobil public takip, boş/hata/yetki/bayat durum örnekleri, açık/koyu tema.

## 11. Uygulama notları (Dilim 9)

> **Karar değişikliği (Dilim 9):** Arayüz `src/web` altında uygulanmıştır ([ADR-019](adr/ADR-019-spa-hosting-ui-stack-and-e2e.md)).
> Tasarımdan bilinçli farklar: (1) istisna kuyruğu görünüm düğmelerinde sayaç yok (sayım ucu yok, uydurma sayı gösterilmez);
> (2) zaman çizelgesinde koli taramaları "N koli taraması" olarak gruplanır (olay koli kimliği taşımaz); (3) hub görevlisi için ayrı
> **Tarama** ekranı (`/scan`) eklendi; (4) sıralanabilir sütunlara "Oluşturma" eklendi; (5) istisna satırında klavye ile `Esc` paneli
> kapatır, `↑/↓` satır gezinmesi uygulanmadı (satır işlemleri odaklanabilir butonlardır); (6) canlı bağlantı yokken üst şerit
> "Canlı güncelleme kapalı · son yenileme" gösterir, 5 dakikadan eski veri uyarı tonuna geçer. Bu dilimde ortamda frontend/erişilebilirlik
> skill'i bulunmadığı için skill çağrılmadı; §1'deki ilkeler ve token'lar aynen kullanıldı.
