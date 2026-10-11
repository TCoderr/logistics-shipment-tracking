import { expect, test } from "@playwright/test";
import { ApiSession, loginViaUi, runTag } from "./support";

test.describe("liste, filtre, arama ve detay", () => {
  let standard: { id: string; trackingNumber: string; reference: string };
  let express: { id: string; trackingNumber: string; reference: string };

  test.beforeAll(async () => {
    const ops = await ApiSession.login("ops.agent");
    const hubIzm = await ApiSession.login("hub.izm");
    try {
      const first = runTag("LIST-STD");
      const second = runTag("LIST-EXP");
      const a = await ops.createShipment({ externalReference: first, origin: "IST-01", destination: "ANK-02" });
      const b = await ops.createShipment({ externalReference: second, origin: "IZM-01", destination: "ANK-02", serviceLevel: "Express" });
      standard = { id: a.id, trackingNumber: a.trackingNumber, reference: first };
      express = { id: b.id, trackingNumber: b.trackingNumber, reference: second };
      // İkinci gönderi İzmir hub'ında: durum filtresi için gerçek bir tarama.
      await hubIzm.scan("IZM-01", b.packages[0]!.labelCode, "Arrival");
    } finally {
      await ops.dispose();
      await hubIzm.dispose();
    }
  });

  test("filtreler URL'de tutulur, birlikte çalışır, temizlenir ve boş sonuç açıklanır", async ({ page }) => {
    await loginViaUi(page, "ops.agent");
    const table = page.getByRole("table", { name: "Gönderiler" });
    await expect(table.getByRole("link", { name: standard.trackingNumber })).toBeVisible();

    const filters = page.getByRole("group", { name: "Filtreler" });
    await filters.getByLabel("Hub").selectOption("IZM-01");
    await expect(page).toHaveURL(/hubCode=IZM-01/);
    await expect(table.getByRole("link", { name: express.trackingNumber })).toBeVisible();
    await expect(table.getByRole("link", { name: standard.trackingNumber })).toHaveCount(0);

    await filters.getByLabel("Durum").selectOption("AtHub");
    await filters.getByLabel("Servis").selectOption("Express");
    await expect(page).toHaveURL(/status=AtHub/);
    await expect(table.getByRole("link", { name: express.trackingNumber })).toBeVisible();
    const row = table.getByRole("row").filter({ hasText: express.trackingNumber });
    await expect(row).toContainText("Hub'da");
    await expect(row).toContainText("IZM-01 → ANK-02");

    // Paylaşılabilir URL: sayfa yenilenince aynı filtreler geçerlidir.
    await page.reload();
    await expect(page.getByText("Hub: IZM-01")).toBeVisible();
    await expect(page.getByText("Durum: Hub'da")).toBeVisible();
    await expect(table.getByRole("link", { name: express.trackingNumber })).toBeVisible();

    // Bu kombinasyonda sonuç yok → filtre kaynaklı boş durum.
    await filters.getByLabel("Durum").selectOption("Delivered");
    await page.getByRole("button", { name: "Durum: Hub'da filtresini kaldır" }).click();
    await filters.getByLabel("Hub").selectOption("IST-01");
    await filters.getByLabel("Servis").selectOption("Express");
    await filters.getByLabel("Gecikmiş").check();
    await expect(page.getByText("Bu filtrelerle gönderi yok.")).toBeVisible();

    await page.getByRole("button", { name: "Filtreleri temizle" }).first().click();
    await expect(page).toHaveURL(/\/shipments$/);
    await expect(table.getByRole("link", { name: standard.trackingNumber })).toBeVisible();
  });

  test("sipariş referansıyla arama listeyi daraltır; tracking numarası doğrudan detaya götürür; '/' aramayı odaklar", async ({ page }) => {
    await loginViaUi(page, "ops.agent");
    const table = page.getByRole("table", { name: "Gönderiler" });
    await expect(table).toBeVisible();

    await page.keyboard.press("/");
    await expect(page.getByLabel("Gönderi ara")).toBeFocused();
    await page.keyboard.type(standard.reference);
    await page.keyboard.press("Enter");
    await expect(page).toHaveURL(new RegExp(`q=${standard.reference}`));
    await expect(table.getByRole("row")).toHaveCount(2);
    await expect(table.getByRole("link", { name: standard.trackingNumber })).toBeVisible();

    // Telefonun son 4 hanesi tek başına aranamaz: sunucu kuralı arayüzde açıklanır.
    await page.getByLabel("Gönderi ara").fill("0199");
    await page.keyboard.press("Enter");
    await expect(page.getByText(/Telefonun son 4 hanesiyle arama için en az bir filtre daha seçin/)).toBeVisible();

    await page.getByLabel("Gönderi ara").fill(express.trackingNumber);
    await page.keyboard.press("Enter");
    await expect(page).toHaveURL(new RegExp(`/shipments/${express.id}$`));
    await expect(page.getByRole("heading", { level: 1 })).toHaveText(express.trackingNumber);
    await expect(page.locator(".detail-head .status")).toHaveText("Hub'da");
    // Zaman çizelgesi: en yeni olay üstte, hub kodu ve kaynak görünür.
    const events = page.locator(".timeline li");
    await expect(events).toHaveCount(2);
    await expect(events.first()).toContainText("Hub'a ulaştı");
    await expect(events.first()).toContainText("IZM-01");
    await expect(events.first()).toContainText("Hub taraması");
    await expect(events.last()).toContainText("Kaydedildi");
  });

  test("sıralama başlığı aria-sort ile durumu bildirir ve sunucu sıralamasını uygular", async ({ page }) => {
    await loginViaUi(page, "ops.agent");
    const table = page.getByRole("table", { name: "Gönderiler" });
    const created = table.getByRole("columnheader", { name: "Oluşturma" });
    await expect(created).toHaveAttribute("aria-sort", "descending");

    await created.getByRole("button").click();
    await expect(page).toHaveURL(/sort=createdAt/);
    await expect(created).toHaveAttribute("aria-sort", "ascending");

    const promised = table.getByRole("columnheader", { name: "Söz" });
    await promised.getByRole("button").click();
    await expect(promised).toHaveAttribute("aria-sort", "ascending");
    await expect(created).toHaveAttribute("aria-sort", "none");
    const dates = await table.locator("tbody tr td:nth-child(3)").allTextContents();
    expect(dates.length).toBeGreaterThan(1);
  });

  test("operasyon özeti gerçek veritabanı sayılarını gösterir (API ile aynı)", async ({ page }) => {
    const ops = await ApiSession.login("ops.agent");
    await loginViaUi(page, "ops.agent");
    try {
      await page.getByRole("link", { name: "Özet" }).click();
      await expect(page.getByRole("heading", { name: "Operasyon özeti" })).toBeVisible();
      const kpis = page.getByRole("group", { name: "Operasyon göstergeleri" });
      await expect(kpis).toBeVisible();
      const summary = (await (await ops.get("/api/operations/summary")).json()) as {
        dueToday: { total: number };
        delayedOpen: number;
        openExceptions: { count: number };
      };
      await expect(kpis.locator(".kpi").nth(0).locator("dd")).toContainText(String(summary.dueToday.total));
      await expect(kpis.locator(".kpi").nth(1).locator("dd")).toContainText(String(summary.delayedOpen));
      await expect(kpis.locator(".kpi").nth(4).locator("dd")).toContainText(String(summary.openExceptions.count));
      await expect(page.getByText("Sayılar veritabanındaki gerçek kayıtlardan hesaplanır.")).toBeVisible();
    } finally {
      await ops.dispose();
    }
  });
});
