import { expect, test } from "@playwright/test";
import { ApiSession, loginViaUi, runTag, synthetic } from "./support";

test.describe("hub taraması, yetki sınırı ve iptal", () => {
  test("hub görevlisi tarar (uygulandı / tekrar / ret); iptal butonu yoktur ve API de reddeder", async ({ page }) => {
    const ops = await ApiSession.login("ops.agent");
    const hubApi = await ApiSession.login("hub.ist");
    try {
      const shipment = await ops.createShipment({ externalReference: runTag("SCAN"), packages: 2 });
      const [first, second] = shipment.packages.map((pkg) => pkg.labelCode);

      await loginViaUi(page, "hub.ist");
      await expect(page).toHaveURL(/\/scan$/);
      const result = page.locator(".scan-result");

      await page.getByLabel("Koli etiketi").fill(first!);
      await page.getByRole("button", { name: "Taramayı kaydet" }).click();
      await expect(result).toContainText("Uygulandı");
      await expect(result).toContainText("Hub'da · 1/2 koli bu hub'da");
      await expect(page.getByLabel("Koli etiketi")).toBeFocused();

      // Aynı koli tekrar okutulur: iş tekrarı tek etkiyle karşılanır.
      await page.getByLabel("Koli etiketi").fill(first!);
      await page.keyboard.press("Enter");
      await expect(result).toContainText("Tekrar · daha önce kaydedildi");

      // Kolilerin hepsi hub'da değilken çıkış reddedilir ve neden gösterilir.
      await page.getByLabel("Tarama tipi").selectOption("Departure");
      await page.getByLabel("Koli etiketi").fill(first!);
      await page.keyboard.press("Enter");
      await expect(result).toContainText("Reddedildi · Kolilerin hepsi bu hub'da değil");
      await expect(result).toContainText("1/2 koli");

      await page.getByLabel("Tarama tipi").selectOption("Arrival");
      await page.getByLabel("Koli etiketi").fill(second!);
      await page.keyboard.press("Enter");
      await expect(result).toContainText("2/2 koli bu hub'da");

      // Bilinmeyen etiket: varlık sızdırmayan genel ret.
      await page.getByLabel("Koli etiketi").fill("LS0000000000-01");
      await page.keyboard.press("Enter");
      await expect(result).toContainText("Reddedildi · Koli etiketi bulunamadı");

      // Hub görevlisi detayda yalnız kendi yetkisindeki bilgiyi görür: iptal/iade yok, telefon maskeli, takip kodu yok.
      await page.getByRole("region", { name: "Son taramalar tablosu" }).getByRole("link", { name: first! }).first().click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(shipment.trackingNumber);
      await expect(page.locator(".detail-head .status")).toHaveText("Hub'da");
      await expect(page.getByRole("button", { name: "Gönderiyi iptal et" })).toHaveCount(0);
      await expect(page.getByRole("button", { name: "Göndericiye iade başlat" })).toHaveCount(0);
      await expect(page.getByText("Bu gönderi için size açık bir işlem yok.")).toBeVisible();
      const recipient = page.getByRole("region", { name: "Alıcı" });
      await expect(recipient).toContainText("+90532*****99");
      await expect(recipient).not.toContainText(synthetic.recipientPhone);
      await expect(page.getByRole("link", { name: "Public takip sayfasını aç" })).toHaveCount(0);
      await expect(page.getByRole("link", { name: "İstisnalar" })).toHaveCount(0);

      // Butonu gizlemek güvenlik değildir: aynı kullanıcı geçerli CSRF + If-Match ile API'yi doğrudan çağırsa da reddedilir.
      const current = await hubApi.detail(shipment.id);
      expect(current.status).toBe(200);
      const denied = await hubApi.post(`/api/shipments/${shipment.id}/cancel`, { data: { reason: "yetkisiz deneme" }, ifMatch: current.etag });
      expect(denied.status()).toBe(403);
      expect(((await denied.json()) as { code: string }).code).toBe("forbidden");
      expect((await ops.detail(shipment.id)).body.status).toBe("AtHub");
      // Operasyon rolündeki kullanıcı da tarama yapamaz (yalnız hub görevlisi).
      const opsScan = await ops.post("/api/hubs/IST-01/scans", {
        data: { clientScanId: `e2e-${crypto.randomUUID()}`, labelCode: first, scanType: "Arrival", occurredAt: new Date().toISOString() },
      });
      expect(opsScan.status()).toBe(403);
    } finally {
      await ops.dispose();
      await hubApi.dispose();
    }
  });

  test("ETag çakışması: iptal 412 alır, güncel durum yüklenir, yazılan neden korunur; ikinci onayla iptal olur", async ({ page }) => {
    const ops = await ApiSession.login("ops.agent");
    const hubApi = await ApiSession.login("hub.ist");
    try {
      const shipment = await ops.createShipment({ externalReference: runTag("ETAG") });
      await loginViaUi(page, "ops.agent");
      await page.goto(`/shipments/${shipment.id}`);
      await expect(page.locator(".detail-head .status")).toHaveText("Kaydedildi");

      await page.getByRole("button", { name: "Gönderiyi iptal et" }).click();
      const form = page.getByRole("form", { name: "Gönderi iptali" });
      await form.getByLabel("İptal nedeni").fill("Gönderici siparişi iptal etti (E2E)");

      // Operatör formu doldururken gönderi başka bir aktör tarafından değişir (gerçek hub taraması).
      await hubApi.scan("IST-01", shipment.packages[0]!.labelCode, "Arrival");

      await form.getByRole("button", { name: "Gönderiyi iptal et" }).click();
      await expect(page.getByText(/Bu gönderi siz bakarken güncellendi \(Hub'da, \d{2}:\d{2}\)\. Güncel durumu yükledik/)).toBeVisible();
      await expect(page.locator(".detail-head .status")).toHaveText("Hub'da");
      await expect(page.locator(".timeline li").first()).toContainText("Hub'a ulaştı");
      await expect(form.getByLabel("İptal nedeni")).toHaveValue("Gönderici siparişi iptal etti (E2E)");
      expect((await ops.detail(shipment.id)).body.status).toBe("AtHub");

      // Hub'dan çıkmamış gönderi hâlâ iptal edilebilir: güncel ETag ile ikinci onay.
      await form.getByRole("button", { name: "Gönderiyi iptal et" }).click();
      await expect(page.getByText("Gönderi iptal edildi.")).toBeVisible();
      await expect(page.locator(".detail-head .status")).toHaveText("İptal");
      await expect(page.getByText("Gönderi tamamlandı; yapılabilecek işlem yok.")).toBeVisible();
      await expect(page.getByRole("button", { name: "Gönderiyi iptal et" })).toHaveCount(0);
      expect((await ops.detail(shipment.id)).body.status).toBe("Cancelled");
    } finally {
      await ops.dispose();
      await hubApi.dispose();
    }
  });

  test("hub'dan çıkmış gönderide iptal sunulmaz, nedeni açıklanır; eski ETag ile API çağrısı da kabul edilmez", async ({ page }) => {
    const ops = await ApiSession.login("ops.agent");
    const hubApi = await ApiSession.login("hub.ist");
    try {
      const shipment = await ops.createShipment({ externalReference: runTag("DEPARTED") });
      const stale = await ops.detail(shipment.id);
      const label = shipment.packages[0]!.labelCode;
      await hubApi.scan("IST-01", label, "Arrival");
      await hubApi.scan("IST-01", label, "Departure");

      await loginViaUi(page, "ops.agent");
      await page.goto(`/shipments/${shipment.id}`);
      await expect(page.locator(".detail-head .status")).toHaveText("Yolda");
      await expect(page.getByRole("button", { name: "Gönderiyi iptal et" })).toHaveCount(0);
      await expect(page.getByText("İptal yok: gönderi hub'dan çıkış yaptı veya carrier'a devredildi.")).toBeVisible();
      await expect(page.getByRole("button", { name: "Göndericiye iade başlat" })).toBeVisible();

      const staleCancel = await ops.post(`/api/shipments/${shipment.id}/cancel`, { data: { reason: "eski ekran" }, ifMatch: stale.etag });
      expect(staleCancel.status()).toBe(412);
      const fresh = await ops.detail(shipment.id);
      const refused = await ops.post(`/api/shipments/${shipment.id}/cancel`, { data: { reason: "çıkış sonrası" }, ifMatch: fresh.etag });
      expect(refused.status()).toBe(409);
      expect((await refused.json()) as { code: string; returnToSenderAllowed: boolean }).toMatchObject({ code: "shipment-not-cancellable", returnToSenderAllowed: true });
    } finally {
      await ops.dispose();
      await hubApi.dispose();
    }
  });
});
