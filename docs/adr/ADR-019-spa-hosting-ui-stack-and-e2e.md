# ADR-019 Operasyon arayüzü: aynı origin SPA, istemci güvenlik kuralları ve E2E (Dilim 9)

Durum: Kabul edildi (2026-10-10)

## Bağlam
Operasyon paneli ve alıcının mobil takip sayfası gerçek API'ye bağlanmalı. Kimlik doğrulama `__Host-` önekli, `Secure`, `SameSite=Strict`
cookie + antiforgery token'ıdır; CORS yoktur. Public takip kodu bir yetki belgesidir ve URL yolunda taşınır.

## Kararlar
1. **Yığın:** React 19 + TypeScript 6 + Vite 8 (`src/web`). Bağımlılıklar: `react-router-dom` (rota), `@tanstack/react-query` (sunucu durumu,
   işlem sonrası ve dilim 10'da bildirimle tazeleme). Bileşen kütüphanesi, durum yönetim kütüphanesi, CSS çatısı yok; stil tek dosyada,
   token adları `docs/07` ve `design/operations-preview.html` ile aynı. TypeScript 7 yerine 6.0: `typescript-eslint` 8'in desteklediği üst sınır.
2. **Aynı origin:** Üretimde statik çıktı image içinde `wwwroot`'a kopyalanır ve ASP.NET host'u tarafından sunulur (`SpaHosting`).
   Geliştirmede Vite `/api`, `/health`, `/realtime` yollarını HTTPS API'ye proxy'ler (tarayıcı için tek origin; cookie/CSRF değişmeden geçer).
   Yerel çalıştırmada `Spa:DistPath` ile `src/web/dist` doğrudan sunulabilir. Statik çıktı yoksa yalnız API çalışır.
3. **Kabuk güvenliği:** `index.html` her istemci rotasına döner ama `/api`, `/health`, `/realtime`, `/openapi` asla kabuğa düşmez (bilinmeyen API
   yolu 404 ProblemDetails kalır). Belge yanıtları: `Content-Security-Policy` (yalnız `'self'`, inline script/stil yok, `frame-ancestors 'none'`),
   `Referrer-Policy: no-referrer`, `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, kabuk `Cache-Control: no-store` +
   `X-Robots-Tag: noindex`. `/assets` içerik özetli olduğundan `immutable`. İstek yolu loglanmadığı için `/t/{kod}` log'a girmez.
   CSP nedeniyle bileşenlerde inline `style` kullanılmaz.
4. **İstemci gizlilik kuralları:** CSRF token'ı yalnız bellekte tutulur; cookie'ler HttpOnly'dir. `console` kullanımı lint ile yasaktır.
   Public takip sayfası oturum sağlayıcısını hiç kurmaz, iç API çağırmaz, bağlantı içermez; kod başlığa/depolamaya yazılmaz.
   `fetch` her istekte `referrerPolicy: "no-referrer"` ve `cache: "no-store"` kullanır.
5. **Yetki:** Menü ve butonlar yalnız kolaylıktır. İşlem butonları sunucunun `allowedActions` listesinden türetilir; rota korumaları ve
   gizlenen butonlar yanında API reddi E2E ile doğrulanır. Demo simülatör paneli yalnız sunucu keşif ucu (`GET /api/dev/carrier-simulator`,
   Development + `Simulation:Enabled` + OpsManager) yanıt verirse görünür; Production'da route yoktur.
6. **Idempotency (istemci):** bir "işlem" = bir istek gövdesi. Yanıt alınamazsa (ağ hatası, `409 in-progress`, `503 commit-outcome-unknown`)
   aynı gövde **aynı** anahtarla yeniden gönderilir. Alan değişirse yeni işlem sayılır, yeni anahtar üretilir ve kullanıcı açıkça uyarılır
   (önceki istek tamamlandıysa ikinci kayıt oluşabilir). Hub taramasında aynı mantık `clientScanId` + sabit `occurredAt` ile uygulanır.
7. **Eşzamanlılık:** `If-Match` komutlarında 412 alınırsa güncel durum sunucudan yeniden okunur, kullanıcının yazdığı metin korunur ve
   çakışma açıklanır; otomatik yeniden gönderim yapılmaz. Kapsam dışına çıkan gönderide (404/403) eldeki eski veri gösterilmez.
8. **Arayüz destek uçları (dar eklemeler):** `GET /api/reference/hubs` (iç kullanıcı), `GET /api/reference/carriers` (operasyon),
   istisna kuyruğunda `shipmentId` filtresi (cursor parmak izine dahil). Hepsi salt-okunur ve kişisel veri içermez.
9. **E2E:** Playwright, gerçek tarayıcı, gerçek API ve **ayrı** Compose projesi/volume'ları (`lst-e2e`). Başarı yolları mock'lanmaz;
   yalnız "yanıt kaybı" ve "ağ hatası" yolları ağ kesilerek üretilir (ilk durumda istek sunucuda gerçekten işlenir). Trace ve video kapalıdır
   (cookie, CSRF token'ı, parola artifact'a girmesin). Demo parolaları özel dosyalardan okunur; veriler `E2E-…` etiketli ve sentetiktir.
   Erişilebilirlik: axe (WCAG 2.x A/AA) açık ve koyu temada, klavye akışı ve üç genişlikte taşma kontrolü.

## Skill kullanımı
Ortamda frontend/erişilebilirlik için özel bir skill yoktur (`artifact-design` ve `dataviz` yayın/grafik odaklıdır). Arayüz, bu skill'lerin
tasarım aşamasında (`docs/07 §1`) dar kapsamda uygulanmış çıktısı olan token'ları ve ilkeleri izler; bu dilimde ayrıca skill çağrılmadı.
Grafik olmadığı için palet doğrulaması gerekmedi. Erişilebilirlik WCAG 2.2 AA ve WAI-ARIA desenleriyle, axe taramasıyla doğrulandı.

## Sonuçlar ve sınırlar
- Windows'ta proje yolu `&` içerdiğinde `node_modules/.bin/*.cmd` sarmalayıcıları bozulur; npm betikleri araçları doğrudan `node` ile çağırır.
- Dev proxy `http://localhost` üzerinden `Secure` cookie'ye dayanır (Chromium/Firefox). Diğer tarayıcılar için dev sunucusu HTTPS'e alınabilir
  (`LST_WEB_HTTPS_PFX`); bu yol doğrulanmadı.
- İstisna kuyruğu sekmelerinde sayaç gösterilmez (sayım ucu yok; uydurma sayı üretilmez). Üst menüdeki rozet ilk sayfadan hesaplanır (`50+`).
- Timeline olayı koli kimliği taşımadığı için ardışık koli taramaları sayıyla ("2 koli taraması") gruplanır, koli bazında açılmaz.
- Remote CI çalıştırılmadı; `web` ve `e2e` işleri yerelde eşdeğer komutlarla (Git Bash'te `scripts/ci-e2e-stack.sh` dahil) doğrulandı.
