import { expect, test, type Page } from "@playwright/test";
import { ApiSession, loginViaUi, runTag, synthetic } from "./support";

async function fillCreateForm(page: Page, externalReference: string): Promise<void> {
  // Boş veritabanında aynı bağlantı boş durum kutusunda da bulunur; araç çubuğundaki kullanılır.
  await page.locator(".toolbar").getByRole("link", { name: "+ Gönderi oluştur" }).click();
  await expect(page.getByRole("heading", { name: "Gönderi oluştur" })).toBeVisible();
  await page.getByLabel("Servis").selectOption("Express");
  await page.getByLabel("Çıkış hub'ı").selectOption("IST-01");
  await page.getByLabel("Varış hub'ı").selectOption("ANK-02");
  await page.getByLabel("Sipariş referansı (isteğe bağlı)").fill(externalReference);

  const sender = page.getByRole("group", { name: "Gönderen" });
  await sender.getByLabel("Ad soyad").fill(synthetic.senderName);
  await sender.getByLabel("Telefon").fill(synthetic.senderPhone);
  await sender.getByLabel("Adres satırı 1").fill(synthetic.senderLine1);
  await sender.getByLabel("Şehir").fill("Istanbul");
  await sender.getByLabel("Posta kodu").fill("34200");

  const recipient = page.getByRole("group", { name: "Alıcı" });
  await recipient.getByLabel("Ad soyad").fill(synthetic.recipientName);
  await recipient.getByLabel("Telefon").fill(synthetic.recipientPhone);
  await recipient.getByLabel("Adres satırı 1").fill(synthetic.recipientLine1);
  await recipient.getByLabel("İlçe (isteğe bağlı)").fill(synthetic.recipientDistrict);
  await recipient.getByLabel("Şehir").fill("Ankara");
  await recipient.getByLabel("Posta kodu").fill(synthetic.recipientPostalCode);

  await page.getByLabel("Koli 1 ağırlık (g)").fill("2400");
  await page.getByLabel("Boy (mm)").fill("400");
  await page.getByLabel("En (mm)").fill("300");
  await page.getByLabel("Yükseklik (mm)").fill("200");
  await page.getByRole("button", { name: "+ Koli ekle" }).click();
  const second = page.getByRole("group", { name: "Koli 2" });
  await second.getByLabel("Koli 2 ağırlık (g)").fill("900");
  await second.getByLabel("Boy (mm)").fill("300");
  await second.getByLabel("En (mm)").fill("200");
  await second.getByLabel("Yükseklik (mm)").fill("100");
}

test.describe("gönderi oluşturma ve yanıt kaybında replay", () => {
  test("sunucu doğrulama hatası alanında görünür; düzeltince gönderi oluşur ve detayda görülür", async ({ page }) => {
    const reference = runTag("CREATE");
    await loginViaUi(page, "ops.agent");
    await fillCreateForm(page, reference);
    const recipient = page.getByRole("group", { name: "Alıcı" });
    await recipient.getByLabel("Telefon").fill("05325550199");

    await page.getByRole("button", { name: "Gönderiyi oluştur" }).click();
    await expect(recipient.getByLabel("Telefon")).toHaveAttribute("aria-invalid", "true");
    await expect(page.getByText(/1 alan düzeltilmeli/)).toBeVisible();

    await recipient.getByLabel("Telefon").fill(synthetic.recipientPhone);
    await page.getByRole("button", { name: "Gönderiyi oluştur" }).click();

    await expect(page).toHaveURL(/\/shipments\/[0-9a-f-]{36}$/);
    await expect(page.getByText("Gönderi oluşturuldu.")).toBeVisible();
    await expect(page.getByRole("heading", { level: 1 })).toHaveText(/^LS[0-9]{10}$/);
    await expect(page.locator(".detail-head .status")).toHaveText("Kaydedildi");
    await expect(page.getByText(reference)).toBeVisible();
    await expect(page.getByRole("heading", { name: "Paketler (2)" })).toBeVisible();
    await expect(page.locator(".timeline li")).toHaveCount(1);
    await expect(page.locator(".timeline li").first()).toContainText("Kaydedildi");
    // Operasyon görevlisi alıcı telefonunu tam görür.
    await expect(page.getByRole("region", { name: "Alıcı" })).toContainText(synthetic.recipientPhone);
    await expect(page.getByRole("button", { name: "Gönderiyi iptal et" })).toBeVisible();
  });

  test("201 yanıtı kaybolursa aynı Idempotency-Key ile yeniden denenir: tek gönderi oluşur, sonuç replay olarak döner", async ({ page }) => {
    const reference = runTag("REPLAY");
    await loginViaUi(page, "ops.agent");
    await fillCreateForm(page, reference);

    // İlk POST sunucuya ULAŞIR ve işlenir; yanıt tarayıcıya iletilmeden bağlantı koparılır.
    const keys: string[] = [];
    let dropped = false;
    await page.route("**/api/shipments", async (route) => {
      const request = route.request();
      if (request.method() !== "POST") return route.continue();
      keys.push(request.headers()["idempotency-key"] ?? "");
      if (!dropped) {
        dropped = true;
        const upstream = await route.fetch();
        expect(upstream.status()).toBe(201);
        return route.abort("connectionreset");
      }
      return route.continue();
    });

    await page.getByRole("button", { name: "Gönderiyi oluştur" }).click();
    await expect(page.getByText(/Yanıt alınamadı\. İstek sunucuya ulaşmış olabilir/)).toBeVisible();

    await page.getByRole("button", { name: "Aynı işlemi yeniden dene" }).click();
    await expect(page).toHaveURL(/\/shipments\/[0-9a-f-]{36}$/);
    await expect(page.getByText("Önceki isteğin sonucu döndü; ikinci bir kayıt oluşmadı.")).toBeVisible();

    expect(keys).toHaveLength(2);
    expect(keys[0]).toMatch(/^[A-Za-z0-9_-]{16,128}$/);
    expect(keys[1]).toBe(keys[0]);

    // Sunucudaki gerçek durum: bu referansla tam bir gönderi var.
    const ops = await ApiSession.login("ops.agent");
    try {
      const list = (await (await ops.get(`/api/shipments?q=${reference}`)).json()) as { items: { id: string }[] };
      expect(list.items).toHaveLength(1);
      expect(page.url()).toContain(list.items[0]!.id);
    } finally {
      await ops.dispose();
    }
  });
});
