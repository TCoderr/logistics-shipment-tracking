import { mkdirSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { expect, test, type Page } from "@playwright/test";
import { ApiSession, driveToOutForDelivery, loginViaUi, runTag } from "./support";

// Tüm ekranların masaüstü / tablet / mobil görüntüleri (görsel inceleme için) ve taşma kontrolü.
// Görüntüler yalnız sentetik E2E verisi içerir; parola alanı tarayıcıda maskelidir.
const here = path.dirname(fileURLToPath(import.meta.url));
const outDir = process.env.LST_E2E_SCREEN_DIR ?? path.resolve(here, "../test-results/screens");
const viewports = [
  { name: "desktop", width: 1440, height: 900 },
  { name: "tablet", width: 820, height: 1180 },
  { name: "mobile", width: 390, height: 844 },
] as const;

async function capture(page: Page, name: string): Promise<void> {
  for (const viewport of viewports) {
    await page.setViewportSize({ width: viewport.width, height: viewport.height });
    // Yatay kaydırma yalnız tablo kabında olabilir; belge taşmaz.
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
    expect(overflow, `${name} @ ${viewport.name}: belge yatay taşıyor`).toBeLessThanOrEqual(1);
    await page.screenshot({ path: path.join(outDir, `${name}-${viewport.name}.png`), fullPage: true });
  }
  await page.setViewportSize({ width: 1440, height: 900 });
}

test("ekran görüntüleri: tüm ekranlar üç genişlikte taşmadan çizilir", async ({ page, browser }) => {
  test.setTimeout(180_000);
  mkdirSync(outDir, { recursive: true });
  const ops = await ApiSession.login("ops.agent");
  try {
    const shipment = await ops.createShipment({ externalReference: runTag("SCREEN"), packages: 2 });
    await driveToOutForDelivery(shipment.packages.map((pkg) => pkg.labelCode));
    const failed = await ops.post(`/api/shipments/${shipment.id}/delivery-attempts`, {
      idempotencyKey: `e2e-${crypto.randomUUID()}`,
      data: { outcome: "Failed", failureReason: "RecipientAbsent", occurredAt: new Date().toISOString(), note: null },
    });
    expect(failed.status()).toBe(201);

    await page.goto("/login");
    await expect(page.getByRole("button", { name: "Giriş yap" })).toBeVisible();
    await capture(page, "01-login");

    await loginViaUi(page, "ops.manager");
    await expect(page.getByRole("table", { name: "Gönderiler" })).toBeVisible();
    await capture(page, "02-list");

    await page.goto("/shipments?status=Delivered&hubCode=IZM-01&delayed=true");
    await expect(page.getByText("Bu filtrelerle gönderi yok.")).toBeVisible();
    await capture(page, "03-list-empty");

    await page.goto("/shipments/new");
    await expect(page.getByLabel("Çıkış hub'ı")).toBeVisible();
    await page.getByRole("button", { name: "Gönderiyi oluştur" }).click();
    await expect(page.getByText(/alan düzeltilmeli/)).toBeVisible();
    await capture(page, "04-create-validation");

    await page.goto(`/shipments/${shipment.id}`);
    await expect(page.locator(".detail-head .status")).toHaveText("Teslim edilemedi");
    await page.getByRole("button", { name: "Göndericiye iade başlat" }).click();
    await capture(page, "05-detail");

    await page.goto(`/exceptions?shipmentId=${shipment.id}`);
    await page.getByRole("button", { name: /^Çöz:/ }).click();
    await capture(page, "06-exceptions");

    await page.goto("/summary");
    await expect(page.getByRole("group", { name: "Operasyon göstergeleri" })).toBeVisible();
    await capture(page, "07-summary");

    await page.getByRole("button", { name: "Koyu tema" }).click();
    await page.goto(`/shipments/${shipment.id}`);
    await expect(page.locator(".detail-head .status")).toHaveText("Teslim edilemedi");
    await capture(page, "08-detail-dark");
    await page.getByRole("button", { name: "Koyu tema" }).click();
    await page.getByRole("button", { name: "Çıkış" }).click();

    await loginViaUi(page, "hub.ank");
    await page.getByLabel("Koli etiketi").fill(shipment.packages[0]!.labelCode);
    await page.keyboard.press("Enter");
    await expect(page.locator(".scan-result")).toBeVisible();
    await capture(page, "09-scan");
    await page.goto("/exceptions");
    await expect(page.getByRole("alert")).toContainText("yetkiniz yok");
    await capture(page, "10-forbidden");

    const anonymous = await browser.newContext({ ignoreHTTPSErrors: true });
    const publicPage = await anonymous.newPage();
    await publicPage.goto(`/t/${shipment.publicTrackingCode}`);
    await expect(publicPage.locator(".pub-status .status")).toBeVisible();
    await capture(publicPage, "11-public");
    await publicPage.goto("/t/00000000000000000000000000");
    await expect(publicPage.getByRole("alert")).toBeVisible();
    await capture(publicPage, "12-public-not-found");
    await anonymous.close();
  } finally {
    await ops.dispose();
  }
});
