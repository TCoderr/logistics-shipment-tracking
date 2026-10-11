import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";
import { ApiSession, collectConsole, driveToOutForDelivery, loginViaUi, runTag, synthetic } from "./support";

const allowedKeys = ["destinationCity", "isDelayed", "lastUpdatedAt", "packageCount", "promisedDeliveryDate", "status", "statusLabel", "timeline", "trackingNumberMasked"];

test.describe("public takip (mobil, oturumsuz)", () => {
  test.use({ viewport: { width: 360, height: 740 }, hasTouch: true, isMobile: true });

  test("alıcı PII görmeden takip eder: yanıt allowlist'tir, sayfada ve istekte kişisel veri/Referer yoktur", async ({ browser, page }) => {
    const ops = await ApiSession.login("ops.agent");
    const reference = runTag("PUBLIC");
    try {
      const shipment = await ops.createShipment({ externalReference: reference, packages: 2 });
      await driveToOutForDelivery(shipment.packages.map((pkg) => pkg.labelCode));

      // Oturumsuz, temiz tarayıcı bağlamı (cookie yok).
      const consoleLines = collectConsole(page);
      const apiRequests: { url: string; referer: string | undefined; cookie: string | undefined }[] = [];
      page.on("request", (request) => {
        if (request.url().includes("/api/")) apiRequests.push({ url: request.url(), referer: request.headers()["referer"], cookie: request.headers()["cookie"] });
      });
      const responsePromise = page.waitForResponse((response) => response.url().includes("/api/public/tracking/"));
      const shell = await page.goto(`/t/${shipment.publicTrackingCode}`);
      const response = await responsePromise;

      // Belge ve API yanıtı: önbellek yok, referrer yok, dizinleme yok.
      expect(shell?.headers()["referrer-policy"]).toBe("no-referrer");
      expect(shell?.headers()["cache-control"]).toBe("no-store");
      expect(shell?.headers()["x-robots-tag"]).toContain("noindex");
      expect(response.status()).toBe(200);
      expect(response.headers()["cache-control"]).toBe("no-store");
      expect(response.headers()["referrer-policy"]).toBe("no-referrer");
      const body = (await response.json()) as Record<string, unknown>;
      expect(Object.keys(body).sort()).toEqual(allowedKeys);

      await expect(page.getByRole("heading", { name: "Gönderi takibi" })).toBeVisible();
      await expect(page.locator(".pub-status .status")).toHaveText("Dağıtımda");
      await expect(page.getByText("2 koli")).toBeVisible();
      const masked = `${shipment.trackingNumber.slice(0, 4)}******${shipment.trackingNumber.slice(-2)}`;
      await expect(page.getByText(masked)).toBeVisible();
      await expect(page.locator(".pub-tl li").first()).toContainText("Dağıtıma çıktı");
      await expect(page.getByText("Ad, adres ve telefon bilgileri bu sayfada gösterilmez.")).toBeVisible();

      // Sayfa metni, HTML ve ham yanıt hiçbir kişisel veri, iç kod veya tam tracking numarası içermez.
      const html = await page.content();
      const raw = JSON.stringify(body);
      for (const secret of [
        synthetic.recipientName,
        synthetic.recipientPhone,
        synthetic.recipientLine1,
        synthetic.recipientDistrict,
        synthetic.recipientPostalCode,
        synthetic.senderName,
        synthetic.senderPhone,
        synthetic.senderLine1,
        reference,
        shipment.trackingNumber,
        shipment.id,
        "IST-01",
        "ANK-02",
        "RecipientAbsent",
      ]) {
        expect(html, `HTML içinde olmamalı: ${secret.slice(0, 4)}…`).not.toContain(secret);
        expect(raw, `yanıtta olmamalı: ${secret.slice(0, 4)}…`).not.toContain(secret);
      }

      // Kod bir yetki belgesidir: başlıkta, depolamada, console'da, dış bağlantıda veya Referer'da yer almaz.
      expect(await page.title()).not.toContain(shipment.publicTrackingCode);
      expect(await page.evaluate(() => JSON.stringify({ ...localStorage, ...sessionStorage }))).not.toContain(shipment.publicTrackingCode);
      expect(consoleLines.join("\n")).not.toContain(shipment.publicTrackingCode);
      expect(await page.locator("a[href]").count()).toBe(0);
      expect(apiRequests.map((request) => new URL(request.url).pathname)).toEqual([`/api/public/tracking/${shipment.publicTrackingCode}`]);
      // Başlık hiç gönderilmez (tarayıcı sürümüne göre yok veya boş).
      expect(apiRequests[0]?.referer ?? "").toBe("");
      expect(apiRequests[0]?.cookie ?? "").toBe("");

      // Mobil: yatay taşma yok, dokunma hedefi yeterli, erişilebilirlik ihlali yok.
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
      expect(overflow).toBeLessThanOrEqual(0);
      const refreshBox = await page.getByRole("button", { name: "Yenile" }).boundingBox();
      expect(refreshBox?.height ?? 0).toBeGreaterThanOrEqual(44);
      const axe = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"]).analyze();
      expect(axe.violations.map((violation) => `${violation.id}: ${violation.help}`)).toEqual([]);

      // İç oturum açık olsa bile public sayfa iç API'yi çağırmaz ve operasyon kabuğunu göstermez.
      const internal = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 360, height: 740 } });
      const internalPage = await internal.newPage();
      await loginViaUi(internalPage, "ops.agent");
      const calls: string[] = [];
      internalPage.on("request", (request) => {
        if (request.url().includes("/api/")) calls.push(new URL(request.url()).pathname);
      });
      await internalPage.goto(`/t/${shipment.publicTrackingCode}`);
      await expect(internalPage.locator(".pub-status .status")).toHaveText("Dağıtımda");
      expect(calls).toEqual([`/api/public/tracking/${shipment.publicTrackingCode}`]);
      await expect(internalPage.getByRole("navigation")).toHaveCount(0);
      await internal.close();
    } finally {
      await ops.dispose();
    }
  });

  test("geçersiz ve bulunamayan kod aynı nötr mesajı verir; kod yenilenince eski bağlantı çalışmaz", async ({ page }) => {
    const ops = await ApiSession.login("ops.agent");
    const manager = await ApiSession.login("ops.manager");
    try {
      const shipment = await ops.createShipment({ externalReference: runTag("ROTATE") });

      await page.goto("/t/not-a-valid-code");
      const invalidText = await page.getByRole("alert").innerText();
      expect(invalidText).toContain("Gönderi bulunamadı.");
      await page.goto("/t/00000000000000000000000000");
      await expect(page.getByRole("alert")).toHaveText(invalidText);

      await page.goto(`/t/${shipment.publicTrackingCode}`);
      await expect(page.locator(".pub-status .status")).toHaveText("Kayıt oluşturuldu");

      // Yalnız yönetici yeniler; görevli API'den reddedilir.
      const current = await ops.detail(shipment.id);
      expect((await ops.post(`/api/shipments/${shipment.id}/public-tracking-code/rotate`, { ifMatch: current.etag })).status()).toBe(403);
      const rotated = await manager.post(`/api/shipments/${shipment.id}/public-tracking-code/rotate`, { ifMatch: current.etag });
      expect(rotated.status()).toBe(200);
      const fresh = ((await rotated.json()) as { publicTrackingCode: string }).publicTrackingCode;
      expect(fresh).not.toBe(shipment.publicTrackingCode);

      await page.getByRole("button", { name: "Yenile" }).click();
      await expect(page.getByRole("alert")).toHaveText(invalidText);
      await page.goto(`/t/${fresh}`);
      await expect(page.locator(".pub-status .status")).toHaveText("Kayıt oluşturuldu");
    } finally {
      await ops.dispose();
      await manager.dispose();
    }
  });
});
