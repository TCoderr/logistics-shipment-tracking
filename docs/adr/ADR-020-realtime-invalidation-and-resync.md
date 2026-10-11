# ADR-020 Canlı güncelleme: SignalR ile geçersizleme ipuçları, yetki iptali ve HTTP ile yeniden eşitleme (Dilim 10)

Durum: Kabul edildi (2026-10-10)

## Bağlam
Operasyon paneli, başka bir aktörün (hub taraması, carrier olayı, başka görevli) yaptığı değişikliği yenilemeden görmeli. Kanal kişisel veri
taşımamalı, yetkisi sonradan kaldırılan kullanıcıya bilgi sızdırmamalı ve bağlantı kopukluğu veri kaybı anlamına gelmemeli.
docs/05 §7 bir değişiklik akışı (`GET /api/shipments/changes?after=<changeSeq>`) öngörüyordu.

## Kararlar
1. **Kanal yalnız ipucudur, DB otoritedir.** Sunucu → istemci mesajları: `ShipmentChanged { shipmentId, status }`,
   `ExceptionChanged { exceptionId, shipmentId, status }`, `SubscriptionRevoked { kind, key }`. Ad, adres, telefon, tracking numarası, referans,
   not veya kullanıcı kimliği yoktur. İstemci mesajdaki değeri ekrana yazmaz; ilgili sorguları HTTP ile yeniden okur (yetki ve alan görünürlüğü
   her zaman normal API'den geçer).
2. **Kaynak outbox'tır.** Mesajlar `IRealtimeNotifier` üzerinden, outbox dağıtıcısı tarafından commit'ten **sonra** yayınlanır. API komutunun
   başarısı yayına bağlı değildir. Yayın hata verirse outbox mesajı işlenmemiş kalır, geri çekilerek yeniden denenir, sonunda dead-letter olur
   ve yetkili replay ile yeniden kuyruğa alınabilir. Teslim en-az-bir-kezdir: yinelenen ipucu zararsızdır. İstisna ataması için
   `shipment.exception-assigned.v1` mesajı eklendi (atanan kullanıcı kimliği yazılmaz).
3. **Değişiklik akışı (changes feed) yok — bilinçli sapma.** Sıra numarası commit sırası değildir; "son N kayıt / son birkaç saniye" gibi geriye
   bakışlar kayıpsız yakalama garantisi vermez. Doğru bir akış anlık görüntü (snapshot) semantiği gerektirirdi; bunun yerine istemci
   **(yeniden) bağlandığında ve ilk abonelikte etkin tüm sorguları (detay, liste, kuyruk, KPI) sunucudan yeniden okur.** Ek olarak 60 sn'de bir
   periyodik mutabakat vardır (kaçan mesaj ve durum değiştirmeyen güncellemeler için). İstemci bir akış konumu tutmaz.
4. **Sunucu yönetimli abonelik.** Hub (`/realtime/operations`) yalnız `SubscribeOperations`, `SubscribeHub(hubCode)`,
   `SubscribeShipment(id)`, `UnsubscribeShipment(id)` sunar; grup adları sunucuda üretilir (`ops`, `hub:{id}`, `shipment:{id}`), istemci grup adı
   veremez. Her abonelik güncel DB rolü/hub ataması ve gönderi kapsamıyla (detay okuma ile aynı kural) yetkilendirilir. Yetkisiz ve bulunamayan
   gönderi aynı `not-found` yanıtını alır. Bağlantı başına en fazla 32 abonelik.
5. **Mevcut bağlantının iptali.** Cookie'deki claim'ler veya güvenlik damgası grup üyeliğini kendiliğinden temizlemez. Bu yüzden:
   - `RealtimeRevalidator` her `Realtime:RevalidateSeconds` (varsayılan 15) saniyede açık bağlantıları güncel DB durumuna göre yeniden doğrular:
     kimlik bileti süresi dolan veya hiç rolü kalmayan (kilitli dahil) kullanıcının bağlantısı kapatılır; artık yetkili olunmayan abonelik gruptan
     çıkarılır ve istemciye `SubscriptionRevoked` gönderilir. **İptal anlık değildir: gecikme üst sınırı bu aralıktır.**
   - Çıkış (`POST /api/auth/logout`) kullanıcının tüm canlı bağlantılarını kapatır (kullanıcının başka oturumu varsa onun istemcisi geçerli
     cookie'siyle yeniden bağlanır).
   - Bağlantı, kurulduğu andaki kimlik biletinin bitiş zamanından sonra açık tutulmaz (kayan süre uzatması bağlantıya yansımaz; istemci yeniden bağlanır).
6. **Aynı origin.** Hub kimlik gerektirir (anonim `401`). Cookie'ler `SameSite=Strict`'tir; ek olarak `/realtime` isteklerinde `Origin` başlığı
   varsa isteğin host'uyla eşleşmelidir (aksi `403`). URL'de veya depoda token taşınmaz.
7. **İstemci.** Bağlantı durumu şeritte gösterilir: *kuruluyor / açık / koptu-yeniden bağlanılıyor / kapalı*. "Canlı güncelleme açık" yalnız
   bağlantı kuruluyken yazılır; kopukken verinin bayat olabileceği söylenir ve yoklamayla tazeleme canlı kanal başarısı gibi sunulmaz.
   İpuçları 250 ms'de birleştirilir. Bağlantı kopunca ve reddedilince oturum HTTP ile yeniden yoklanır (süresi dolmuşsa giriş ekranı).

## Sonuçlar ve sınırlar
- **Tek instance.** Bağlantı kaydı ve SignalR grupları bellek içidir; backplane yoktur. Birden çok API instance'ı için backplane veya yapışkan
  oturum + dağıtık iptal gerekir (yapılmadı; Redis eklenmedi).
- **Kapsanmayan ipuçları.** Outbox mesajı yalnız durum değişikliği ve istisna aç/ata/çöz için üretilir. Durumu değiştirmeyen güncellemeler
  (yalnız koli konumu taraması, carrier ataması, takip kodu yenileme, istisna önem yükseltmesi) canlı ipucu üretmez; periyodik mutabakat ve
  elle yenileme ile görünür. `shipment.exception-resolved.v1` hub kimliği taşımadığı için hub grubuna gitmez (operasyon ve gönderi gruplarına gider).
- **Durumsuz cookie.** Çıkış, sunucu tarafında bileti geçersiz kılmaz (Dilim 1 tasarımı); kopyalanmış bir cookie süresi dolana kadar hem API'de hem
  hub'da geçerlidir. Çıkıştaki bağlantı kapatma yalnız açık bağlantıları keser.
- **Sıra.** Mesajlar sırasız veya yinelenmiş gelebilir; istemci sıraya güvenmez.
- **Kapasite.** Bağlantı sayısı/mesaj hızı ölçülmedi; kapasite iddiası yoktur.

## Bu dilimde bulunan ve düzeltilen kusurlar (Dilim 6 kodu)
- Carrier inbox claim sorgusu `UPDATE … RETURNING` sırasına güveniyordu; partideki makbuzlar rastgele sırayla işlenebiliyor ve sıralı gelen akışta
  geçerli olaylar `Stale` sonuçlanabiliyordu → makbuzlar alınış sırasıyla işlenir (en-iyi-çaba; doğruluk yine olay kararlarına dayanır).
- Simülatör, yeni oluşturulmuş gönderide tüm olayları aynı ana (saniyeye yuvarlanmış) yerleştiriyordu → pencere en az 6 sn ve zamanlar milisaniye hassasiyetinde.
