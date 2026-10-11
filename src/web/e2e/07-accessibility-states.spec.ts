import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { ApiSession, driveToOutForDelivery, loginViaUi, runTag } from "./support";

async function expectNoAxeViolations(page: Page, label: string): Promise<void> {
  const result = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"]).analyze();
  expect(result.violations.map((violation) => `${label} → ${violation.id}: ${violation.help} (${violation.nodes.length})`)).toEqual([]);
}

test.describe("erişilebilirlik, klavye ve durum ekranları", () => {
  test("axe: giriş, liste, oluşturma, detay, istisnalar, özet ve tarama ekranlarında WCAG A/AA ihlali yok (açık ve koyu tema)", async ({ page }) => {
    const ops = await ApiSession.login("ops.agent");
    try {
      const shipment = await ops.createShipment({ externalReference: runTag("AXE"), packages: 2 });
      await driveToOutForDelivery(shipment.packages.map((pkg) => pkg.labelCode));
      const failed = await ops.post(`/api/shipments/${shipment.id}/delivery-attempts`, {
        idempotencyKey: `e2e-${crypto.randomUUID()}`,
        data: { outcome: "Failed", failureReason: "RecipientAbsent", occurredAt: new Date().toISOString(), note: null },
      });
      expect(failed.status()).toBe(201);

      await page.goto("/login");
      await expect(page.getByRole("button", { name: "Giriş yap" })).toBeVisible();
      await expectNoAxeViolations(page, "giriş");

      await loginViaUi(page, "ops.manager");
      for (const theme of ["açık", "koyu"] as const) {
        if (theme === "koyu") {
          await page.getByRole("button", { name: "Koyu tema" }).click();
          await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");
        }
        await page.goto("/shipments");
        await expect(page.getByRole("table", { name: "Gönderiler" })).toBeVisible();
        await expectNoAxeViolations(page, `liste (${theme})`);

        await page.goto("/shipments/new");
        await expect(page.getByLabel("Çıkış hub'ı")).toBeVisible();
        await page.getByRole("button", { name: "Gönderiyi oluştur" }).click();
        await expect(page.getByText(/alan düzeltilmeli/)).toBeVisible();
        await expectNoAxeViolations(page, `oluşturma + doğrulama hataları (${theme})`);

        await page.goto(`/shipments/${shipment.id}`);
        await expect(page.locator(".detail-head .status")).toHaveText("Teslim edilemedi");
        await page.getByRole("button", { name: "Göndericiye iade başlat" }).click();
        await expectNoAxeViolations(page, `detay + onay paneli (${theme})`);

        await page.goto(`/exceptions?shipmentId=${shipment.id}`);
        await page.getByRole("button", { name: /^Çöz:/ }).click();
        await expectNoAxeViolations(page, `istisnalar + çözüm paneli (${theme})`);

        await page.goto("/summary");
        await expect(page.getByRole("group", { name: "Operasyon göstergeleri" })).toBeVisible();
        await expectNoAxeViolations(page, `özet (${theme})`);
      }
      await page.getByRole("button", { name: "Koyu tema" }).click();
      await page.getByRole("button", { name: "Çıkış" }).click();

      await loginViaUi(page, "hub.ank");
      await page.getByLabel("Koli etiketi").fill("LS0000000000-01");
      await page.keyboard.press("Enter");
      await expect(page.locator(".scan-result")).toContainText("Reddedildi");
      await expectNoAxeViolations(page, "tarama + sonuç");
    } finally {
      await ops.dispose();
    }
  });

  test("klavye: liste → detay → iade onayı yalnız klavyeyle tamamlanır; odak görünür ve sıra görsel sırayla aynıdır", async ({ page }) => {
    const ops = await ApiSession.login("ops.agent");
    const hub = await ApiSession.login("hub.ist");
    try {
      const reference = runTag("KEYS");
      const shipment = await ops.createShipment({ externalReference: reference });
      await hub.scan("IST-01", shipment.packages[0]!.labelCode, "Arrival");
      await hub.scan("IST-01", shipment.packages[0]!.labelCode, "Departure");

      await loginViaUi(page, "ops.agent");
      await expect(page.getByRole("table", { name: "Gönderiler" })).toBeVisible();

      // İlk Tab: içeriğe geç bağlantısı (görünür olur).
      await page.keyboard.press("Tab");
      await expect(page.getByRole("link", { name: "İçeriğe geç" })).toBeFocused();

      // Arama kısayolu, sonuçta tek satır; tracking bağlantısına klavyeyle ulaşılır.
      await page.keyboard.press("/");
      await page.keyboard.type(reference);
      await page.keyboard.press("Enter");
      const link = page.getByRole("link", { name: shipment.trackingNumber });
      await expect(link).toBeVisible();
      await link.focus();
      const outline = await link.evaluate((element) => getComputedStyle(element).outlineStyle);
      expect(outline).not.toBe("none");
      await page.keyboard.press("Enter");
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(shipment.trackingNumber);

      // İşlem butonu → onay paneli → neden → gönder: fare kullanılmaz.
      const open = page.getByRole("button", { name: "Göndericiye iade başlat" });
      await open.focus();
      await page.keyboard.press("Enter");
      const reason = page.getByLabel("İade nedeni");
      await expect(reason).toBeVisible();
      await page.keyboard.press("Tab");
      await expect(reason).toBeFocused();
      await page.keyboard.type("Gönderici geri istedi (klavye E2E)");
      await page.keyboard.press("Tab");
      await expect(page.getByRole("button", { name: "Göndericiye iadeyi başlat" })).toBeFocused();
      await page.keyboard.press("Enter");
      await expect(page.getByText("Göndericiye iade başlatıldı.")).toBeVisible();
      await expect(page.locator(".detail-head .status")).toHaveText("İadede");

      // g s kısayolu listeye döner.
      await page.locator("main").focus();
      await page.keyboard.press("g");
      await page.keyboard.press("s");
      await expect(page).toHaveURL(/\/shipments$/);
    } finally {
      await ops.dispose();
      await hub.dispose();
    }
  });

  test("yetki ve bulunamadı durumları: gizli menü yanında doğrudan URL ve API de reddedilir", async ({ page }) => {
    const ops = await ApiSession.login("ops.agent");
    const izm = await ApiSession.login("hub.izm");
    try {
      const shipment = await ops.createShipment({ externalReference: runTag("SCOPE"), origin: "IST-01", destination: "ANK-02" });

      await loginViaUi(page, "hub.izm");
      await expect(page.getByRole("link", { name: "İstisnalar" })).toHaveCount(0);
      await expect(page.getByRole("link", { name: "Özet" })).toHaveCount(0);

      for (const path of ["/exceptions", "/summary", "/shipments/new"]) {
        await page.goto(path);
        await expect(page.getByRole("alert")).toContainText("Bu işlem için yetkiniz yok.");
      }
      expect((await izm.get("/api/exceptions")).status()).toBe(403);
      expect((await izm.get("/api/operations/summary")).status()).toBe(403);

      // Kapsam dışı gönderi: var olup olmadığı ayrıştırılmaz.
      await page.goto(`/shipments/${shipment.id}`);
      await expect(page.getByRole("alert")).toHaveText("Gönderi bulunamadı veya erişiminiz yok.");
      await page.goto("/shipments/00000000-0000-7000-8000-000000000000");
      await expect(page.getByRole("alert")).toHaveText("Gönderi bulunamadı veya erişiminiz yok.");
      expect((await izm.detail(shipment.id)).status).toBe(404);

      // Kapsam dışı gönderi listede de yoktur.
      await page.goto("/shipments");
      await expect(page.getByRole("link", { name: shipment.trackingNumber })).toHaveCount(0);

      await page.goto("/olmayan-sayfa");
      await expect(page.getByRole("alert")).toHaveText("Sayfa bulunamadı.");
    } finally {
      await ops.dispose();
      await izm.dispose();
    }
  });

  test("hata ve tazelik: liste yüklenemezse yeniden dene sunulur; son yenileme zamanı ve bağlantı durumu görünür", async ({ page }) => {
    await loginViaUi(page, "ops.agent");
    await expect(page.getByRole("table", { name: "Gönderiler" })).toBeVisible();
    await expect(page.locator(".statusbar")).toContainText("Canlı güncelleme açık");
    await expect(page.getByText(/Son yenileme \d{2}:\d{2}/)).toBeVisible();

    // Yalnız HATA yolu için ağ kesintisi taklit edilir; başarı yolu her zaman gerçek API'dir.
    let failing = true;
    await page.route("**/api/shipments?*", (route) => (failing ? route.abort("connectionfailed") : route.continue()));
    await page.getByRole("group", { name: "Filtreler" }).getByLabel("Hub").selectOption("ANK-02");
    const alert = page.getByRole("alert").filter({ hasText: "Gönderiler yüklenemedi" });
    await expect(alert).toContainText("Bağlantınızı kontrol edip yeniden deneyin.");

    failing = false;
    await alert.getByRole("button", { name: "Yeniden dene" }).click();
    await expect(page.getByRole("table", { name: "Gönderiler" })).toBeVisible();
    await expect(alert).toHaveCount(0);
  });
});
