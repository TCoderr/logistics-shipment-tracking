import { expect, test } from "@playwright/test";
import { ApiSession, driveToOutForDelivery, loginViaUi, logoutViaUi, runTag } from "./support";

test.describe("teslimat denemesi ve istisna kuyruğu", () => {
  test("başarısız teslimat istisna açar; görevli üzerine alır, iade çözümüyle kapatır; kuyruktan düşer ve gönderi iadeye geçer", async ({ page }) => {
    const ops = await ApiSession.login("ops.agent");
    try {
      const shipment = await ops.createShipment({ externalReference: runTag("EXC") });
      await driveToOutForDelivery(shipment.packages.map((pkg) => pkg.labelCode));

      // 1) Varış hub'ı görevlisi başarısız teslimat denemesini arayüzden kaydeder.
      await loginViaUi(page, "hub.ank");
      await page.goto(`/shipments/${shipment.id}`);
      await expect(page.locator(".detail-head .status")).toHaveText("Dağıtımda");
      await page.getByRole("button", { name: "Teslimat denemesi kaydet" }).click();
      const attempt = page.getByRole("form", { name: "Teslimat denemesi" });
      await attempt.getByLabel("Sonuç").selectOption("Failed");
      await attempt.getByLabel("Neden").selectOption("RecipientAbsent");
      await attempt.getByRole("button", { name: "Denemeyi kaydet" }).click();
      await expect(page.getByText("Başarısız teslimat denemesi kaydedildi.")).toBeVisible();
      await expect(page.locator(".detail-head .status")).toHaveText("Teslim edilemedi");
      await expect(page.getByRole("heading", { name: "Teslimat denemeleri 1/3" })).toBeVisible();
      await expect(page.getByRole("region", { name: "Açık istisnalar" })).toContainText("Teslim edilemedi");
      // Hub görevlisi istisna kuyruğuna gidemez (bağlantı yok).
      await expect(page.getByRole("link", { name: "Kuyrukta aç" })).toHaveCount(0);
      await logoutViaUi(page);

      // 2) Operasyon görevlisi detaydaki uyarıdan kuyruğa geçer.
      await loginViaUi(page, "ops.agent");
      await page.goto(`/shipments/${shipment.id}`);
      await expect(page.getByRole("region", { name: "Açık istisnalar" })).toContainText("sahibi yok");
      await page.getByRole("link", { name: "Kuyrukta aç" }).click();
      await expect(page).toHaveURL(new RegExp(`/exceptions\\?shipmentId=${shipment.id}`));
      const queue = page.getByRole("table", { name: "İstisna kuyruğu" });
      const row = queue.getByRole("row").filter({ hasText: shipment.trackingNumber });
      await expect(row).toHaveCount(1);
      await expect(row).toContainText("Teslim edilemedi");
      await expect(row).toContainText("Orta");
      await expect(row).toContainText("ANK-02");

      // Sahipsiz istisna çözülemez: önce üzerine alınmalı.
      await row.getByRole("button", { name: /^Çöz:/ }).click();
      await expect(page.getByText(/Çözebilmek için istisnanın sizde olması gerekir/)).toBeVisible();
      await row.getByRole("button", { name: /^Üzerime al/ }).click();
      await expect(page.getByText("İstisna üzerinize alındı.")).toBeVisible();
      await expect(row).toContainText("Siz");

      // 3) Çözüm: yalnız bu tipe izinli kodlar sunulur; iade çözümü gönderiyi aynı işlemde iadeye geçirir.
      const form = page.getByRole("form", { name: `${shipment.trackingNumber} istisna çözümü` });
      await expect(form.getByLabel("Çözüm", { exact: true }).locator("option")).toHaveText(["Seçin…", "Göndericiye iadeyi başlat", "İşlem gerekmiyor"]);
      await form.getByLabel("Çözüm", { exact: true }).selectOption("ReturnToSenderInitiated");
      await form.getByLabel("Çözüm notu").fill("Alıcıya ulaşılamadı; gönderici iade istedi (E2E).");
      await form.getByRole("button", { name: "Çözümü kaydet" }).click();
      await expect(page.getByText("İstisna çözüldü.")).toBeVisible();

      // Açık kuyruktan düştü; "Benim" görünümünde de yok.
      await expect(queue.getByRole("row").filter({ hasText: shipment.trackingNumber })).toHaveCount(0);
      await expect(page.getByText(/Açık istisna yok\./)).toBeVisible();
      await page.getByRole("button", { name: "Benim" }).click();
      await expect(page.getByRole("link", { name: shipment.trackingNumber })).toHaveCount(0);
      // Çözülenlerde görünür.
      await page.getByRole("button", { name: "Çözülen" }).click();
      const resolved = page.getByRole("table", { name: "İstisna kuyruğu" }).getByRole("row").filter({ hasText: shipment.trackingNumber });
      await expect(resolved).toHaveCount(1);

      // Gönderi sunucuda gerçekten iadeye geçti ve açık istisnası kalmadı.
      await resolved.getByRole("link", { name: shipment.trackingNumber }).click();
      await expect(page.locator(".detail-head .status")).toHaveText("İadede");
      await expect(page.getByRole("region", { name: "Açık istisnalar" })).toHaveCount(0);
      await expect(page.getByRole("button", { name: "İadeyi tamamla" })).toBeVisible();
      const server = await ops.detail(shipment.id);
      expect(server.body.status).toBe("ReturningToSender");
      expect(server.body.openExceptions).toEqual([]);
    } finally {
      await ops.dispose();
    }
  });

  test("istisna başka görevli tarafından alınmışsa eski ekrandan üzerine alma 412 ile reddedilir ve kuyruk güncellenir", async ({ page }) => {
    const ops = await ApiSession.login("ops.agent");
    const manager = await ApiSession.login("ops.manager");
    try {
      const shipment = await ops.createShipment({ externalReference: runTag("EXC412") });
      await driveToOutForDelivery(shipment.packages.map((pkg) => pkg.labelCode));
      const failed = await manager.post(`/api/shipments/${shipment.id}/delivery-attempts`, {
        idempotencyKey: `e2e-${crypto.randomUUID()}`,
        data: { outcome: "Failed", failureReason: "AddressNotFound", occurredAt: new Date().toISOString(), note: null },
      });
      expect(failed.status()).toBe(201);

      await loginViaUi(page, "ops.agent");
      await page.goto(`/exceptions?shipmentId=${shipment.id}`);
      const row = page.getByRole("table", { name: "İstisna kuyruğu" }).getByRole("row").filter({ hasText: shipment.trackingNumber });
      await expect(row.getByRole("button", { name: /^Üzerime al/ })).toBeVisible();

      // Ekran açıkken yönetici istisnayı kendi üzerine alır.
      const list = (await (await manager.get(`/api/exceptions?shipmentId=${shipment.id}`)).json()) as { items: { id: string; etag: string }[] };
      const taken = await manager.post(`/api/exceptions/${list.items[0]!.id}/assign`, { data: { assigneeUserId: manager.userId }, ifMatch: list.items[0]!.etag });
      expect(taken.status()).toBe(200);

      await row.getByRole("button", { name: /^Üzerime al/ }).click();
      await expect(page.getByText("Bu istisna siz bakarken güncellendi. Güncel durumu yükledik; işlemi tekrar gözden geçirin.")).toBeVisible();
      await expect(row).toContainText("Ops Manager (demo)");
      await expect(row.getByRole("button", { name: /^Üzerime al/ })).toHaveCount(0);
    } finally {
      await ops.dispose();
      await manager.dispose();
    }
  });
});
