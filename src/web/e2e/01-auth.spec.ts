import { expect, test } from "@playwright/test";
import { ApiSession, collectConsole, loginViaUi, logoutViaUi, passwordOf } from "./support";

test.describe("giriş, oturum ve CSRF", () => {
  test("oturumsuz kullanıcı girişe yönlenir; hatalı girişte genel hata; doğru girişte korumalı cookie ile liste açılır", async ({ page, context }) => {
    const consoleLines = collectConsole(page);

    await page.goto("/shipments");
    await expect(page).toHaveURL(/\/login$/);
    await expect(page.getByRole("heading", { name: "LST Operasyon" })).toBeVisible();

    // Bilinmeyen kullanıcı: hesap kilidi sayaçlarını etkilemeden genel hata doğrulanır.
    await page.getByLabel("Kullanıcı adı").fill("e2e.bilinmeyen");
    await page.getByLabel("Parola").fill("Yanlis-Parola-000!");
    await page.getByRole("button", { name: "Giriş yap" }).click();
    await expect(page.getByRole("alert")).toContainText("Kullanıcı adı veya parola hatalı ya da hesap geçici olarak kilitli.");
    await expect(page.getByLabel("Parola")).toHaveValue("");

    await page.getByLabel("Kullanıcı adı").fill("ops.agent");
    await page.getByLabel("Parola").fill(passwordOf("ops.agent"));
    await page.getByRole("button", { name: "Giriş yap" }).click();

    // Giriş öncesi istenen sayfaya dönülür.
    await expect(page).toHaveURL(/\/shipments$/);
    await expect(page.locator(".toolbar").getByRole("link", { name: "+ Gönderi oluştur" })).toBeVisible();
    await expect(page.getByText("Operasyon görevlisi")).toBeVisible();

    const cookies = await context.cookies();
    const auth = cookies.find((cookie) => cookie.name === "__Host-lst.auth");
    const antiforgery = cookies.find((cookie) => cookie.name === "__Host-lst.af");
    expect(auth, "auth cookie").toBeDefined();
    expect(auth).toMatchObject({ httpOnly: true, secure: true, sameSite: "Strict", path: "/" });
    expect(antiforgery).toMatchObject({ httpOnly: true, secure: true, sameSite: "Strict" });

    // Script oturum cookie'lerini okuyamaz; token/parola kalıcı depoya veya console'a yazılmaz.
    expect(await page.evaluate(() => document.cookie)).not.toContain("lst.auth");
    const storage = await page.evaluate(() => JSON.stringify({ local: { ...localStorage }, session: { ...sessionStorage } }));
    expect(storage).not.toContain(passwordOf("ops.agent"));
    expect(storage.toLowerCase()).not.toContain("csrf");
    expect(consoleLines.join("\n")).not.toContain(passwordOf("ops.agent"));
    expect(consoleLines.filter((line) => /csrf|password|parola/i.test(line))).toEqual([]);

    await logoutViaUi(page);
    await page.goto("/shipments");
    await expect(page).toHaveURL(/\/login$/);
    expect((await context.cookies()).find((cookie) => cookie.name === "__Host-lst.auth")).toBeUndefined();
  });

  test("CSRF token'sız durum değiştiren istek reddedilir; token ile aynı istek kabul edilir", async () => {
    const ops = await ApiSession.login("ops.agent");
    try {
      const withoutToken = await ops.post("/api/auth/logout", { omitCsrf: true });
      expect(withoutToken.status()).toBe(400);
      expect(((await withoutToken.json()) as { code: string }).code).toBe("csrf-validation-failed");
      expect((await ops.get("/api/auth/me")).status()).toBe(200);

      expect((await ops.post("/api/auth/logout")).status()).toBe(204);
      expect((await ops.get("/api/auth/me")).status()).toBe(401);
    } finally {
      await ops.dispose();
    }
  });

  test("oturum sunucuda sona erince arayüz giriş ekranına döner ve nedenini söyler", async ({ page, context }) => {
    await loginViaUi(page, "ops.agent");
    await expect(page.locator(".toolbar").getByRole("link", { name: "+ Gönderi oluştur" })).toBeVisible();

    await expect(page.locator(".statusbar")).toHaveAttribute("data-connection", "connected");
    await expect(page.getByRole("button", { name: "Yenile" })).toBeEnabled();

    // Cookie süresinin dolmasını temsilen yalnız oturum cookie'si silinir.
    await context.clearCookies({ name: "__Host-lst.auth" });
    await page.getByRole("button", { name: "Yenile" }).click();

    await expect(page).toHaveURL(/\/login$/);
    await expect(page.getByText("Oturumunuz sona erdi. Devam etmek için yeniden giriş yapın.")).toBeVisible();
  });
});
