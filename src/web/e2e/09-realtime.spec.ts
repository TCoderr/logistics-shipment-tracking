import { expect, test, type Page, type WebSocketRoute } from "@playwright/test";
import { ApiSession, driveToOutForDelivery, loginViaUi, logoutViaUi, runTag, synthetic } from "./support";

// Canlı güncelleme E2E. İstemcinin periyodik mutabakatı 60 sn'dir; buradaki beklemeler birkaç saniyedir ve ayrıca
// WebSocket çerçeveleri doğrudan incelenir: yani gözlenen güncelleme yoklama değil, sunucu itmesidir.

interface HubInvocation {
  type: number;
  target?: string;
  arguments?: Record<string, unknown>[];
}

/** Sayfanın canlı hub bağlantısından gelen SignalR çağrı kayıtlarını toplar (JSON protokolü, kayıt ayırıcı 0x1e). */
function collectHubMessages(page: Page) {
  const invocations: HubInvocation[] = [];
  const raw: string[] = [];
  // Sayfa yeniden yüklenince yeni bağlantı açılır; `currentOpen` en son açılan bağlantının hâlâ açık olup olmadığıdır.
  const sockets = { opened: 0, closed: 0, currentOpen: false };
  page.on("websocket", (socket) => {
    if (!socket.url().includes("/realtime/operations")) return;
    sockets.opened += 1;
    sockets.currentOpen = true;
    const order = sockets.opened;
    socket.on("close", () => {
      sockets.closed += 1;
      if (order === sockets.opened) sockets.currentOpen = false;
    });
    socket.on("framereceived", (frame) => {
      const text = typeof frame.payload === "string" ? frame.payload : frame.payload.toString("utf8");
      raw.push(text);
      for (const record of text.split("\u001e").filter(Boolean)) {
        try {
          const parsed = JSON.parse(record) as HubInvocation;
          if (parsed.type === 1) invocations.push(parsed);
        } catch {
          // El sıkışma dışı beklenmeyen çerçeve: yok sayılır.
        }
      }
    });
  });
  return {
    raw,
    sockets,
    of: (target: string, shipmentId: string) => invocations.filter((message) => message.target === target && message.arguments?.[0]?.shipmentId === shipmentId),
    all: () => invocations,
  };
}

const statusBar = (page: Page) => page.locator(".statusbar");

test.describe("canlı güncelleme (SignalR) ve yeniden bağlanma", () => {
  test("oturumsuz istemci canlı hub'a bağlanamaz", async ({ request }) => {
    const negotiate = await request.post("/realtime/operations/negotiate?negotiateVersion=1");
    expect(negotiate.status()).toBe(401);
    expect(((await negotiate.json()) as { code: string }).code).toBe("unauthenticated");
  });

  test("liste yenilenmeden güncellenir: değişiklik WebSocket ile, yalnız kimlik + durum taşıyan ipucu olarak gelir", async ({ page }) => {
    const ops = await ApiSession.login("ops.agent");
    const hub = await ApiSession.login("hub.ist");
    const reference = runTag("LIVE");
    const hubMessages = collectHubMessages(page);
    try {
      const shipment = await ops.createShipment({ externalReference: reference });
      await loginViaUi(page, "ops.agent");
      await page.goto(`/shipments?q=${reference}`);
      const row = page.getByRole("table", { name: "Gönderiler" }).getByRole("row").filter({ hasText: shipment.trackingNumber });
      await expect(row).toContainText("Kaydedildi");
      await expect(statusBar(page)).toHaveAttribute("data-connection", "connected");
      await expect(statusBar(page)).toContainText("Canlı güncelleme açık");

      // Başka bir aktör (hub görevlisi) gönderiyi değiştirir; operatör sayfaya dokunmaz.
      await hub.scan("IST-01", shipment.packages[0]!.labelCode, "Arrival");

      await expect(row).toContainText("Hub'da", { timeout: 8_000 });
      await expect(row).toContainText("IST-01");

      const changed = hubMessages.of("ShipmentChanged", shipment.id);
      expect(changed.length).toBeGreaterThan(0);
      expect(changed.at(-1)?.arguments?.[0]).toEqual({ shipmentId: shipment.id, status: "AtHub" });
      // Hiçbir çerçeve kişisel veri, referans, tracking numarası veya etiket taşımaz.
      const frames = hubMessages.raw.join("\n");
      for (const secret of [synthetic.recipientName, synthetic.recipientPhone, synthetic.recipientLine1, synthetic.senderName, reference, shipment.trackingNumber, shipment.publicTrackingCode]) {
        expect(frames).not.toContain(secret);
      }
      for (const message of hubMessages.all()) {
        expect(["ShipmentChanged", "ExceptionChanged", "SubscriptionRevoked"]).toContain(message.target);
        expect(Object.keys(message.arguments?.[0] ?? {}).every((key) => ["shipmentId", "exceptionId", "status", "kind", "key"].includes(key))).toBe(true);
      }
    } finally {
      await ops.dispose();
      await hub.dispose();
    }
  });

  test("istisna kuyruğu canlıdır: başkasının ataması ve çözümü yenilemeden yansır", async ({ page }) => {
    const ops = await ApiSession.login("ops.agent");
    const manager = await ApiSession.login("ops.manager");
    try {
      const shipment = await ops.createShipment({ externalReference: runTag("LIVEEXC") });
      await driveToOutForDelivery(shipment.packages.map((pkg) => pkg.labelCode));

      await loginViaUi(page, "ops.agent");
      await page.goto(`/exceptions?shipmentId=${shipment.id}`);
      await expect(statusBar(page)).toHaveAttribute("data-connection", "connected");
      await expect(page.getByText(/Açık istisna yok\./)).toBeVisible();

      // Başarısız teslimat başka bir oturumdan kaydedilir: istisna kuyruğa canlı düşer.
      const failed = await manager.post(`/api/shipments/${shipment.id}/delivery-attempts`, {
        idempotencyKey: `e2e-${crypto.randomUUID()}`,
        data: { outcome: "Failed", failureReason: "RecipientRefused", occurredAt: new Date().toISOString(), note: null },
      });
      expect(failed.status()).toBe(201);
      const row = page.getByRole("table", { name: "İstisna kuyruğu" }).getByRole("row").filter({ hasText: shipment.trackingNumber });
      await expect(row).toContainText("Teslim edilemedi", { timeout: 8_000 });
      await expect(row.getByRole("button", { name: /^Üzerime al/ })).toBeVisible();

      // Yönetici üzerine alır: sahibi sütunu canlı güncellenir.
      const list = (await (await manager.get(`/api/exceptions?shipmentId=${shipment.id}`)).json()) as { items: { id: string; etag: string }[] };
      const assigned = await manager.post(`/api/exceptions/${list.items[0]!.id}/assign`, { data: { assigneeUserId: manager.userId }, ifMatch: list.items[0]!.etag });
      expect(assigned.status()).toBe(200);
      await expect(row).toContainText("Ops Manager (demo)", { timeout: 8_000 });
      await expect(row.getByRole("button", { name: /^Üzerime al/ })).toHaveCount(0);

      // Yönetici çözer: istisna açık kuyruktan canlı düşer.
      const resolved = await manager.post(`/api/exceptions/${list.items[0]!.id}/resolve`, {
        data: { resolutionCode: "NoActionNeeded", note: "Alıcıyla görüşüldü (E2E)." },
        ifMatch: assigned.headers()["etag"],
      });
      expect(resolved.status()).toBe(200);
      await expect(row).toHaveCount(0, { timeout: 8_000 });
      await expect(page.getByText(/Açık istisna yok\./)).toBeVisible();
    } finally {
      await ops.dispose();
      await manager.dispose();
    }
  });

  test("hub kapsamı: ilgili hub'ın görevlisi değişikliği canlı görür, başka hub'ın görevlisine ipucu bile gitmez", async ({ browser }) => {
    const ops = await ApiSession.login("ops.agent");
    const istContext = await browser.newContext({ ignoreHTTPSErrors: true });
    const izmContext = await browser.newContext({ ignoreHTTPSErrors: true });
    try {
      const istPage = await istContext.newPage();
      const izmPage = await izmContext.newPage();
      const izmMessages = collectHubMessages(izmPage);
      await loginViaUi(istPage, "hub.ist");
      await loginViaUi(izmPage, "hub.izm");
      await istPage.goto("/shipments");
      await izmPage.goto("/shipments");
      await expect(statusBar(istPage)).toHaveAttribute("data-connection", "connected");
      await expect(statusBar(izmPage)).toHaveAttribute("data-connection", "connected");

      // IST-01 → ANK-02 gönderisi oluşur: hub.ist'in listesine canlı düşer.
      const shipment = await ops.createShipment({ externalReference: runTag("LIVEHUB") });
      await expect(istPage.getByRole("link", { name: shipment.trackingNumber })).toBeVisible({ timeout: 8_000 });

      // hub.izm (IZM-01) için: listede görünmez ve WebSocket'ten bu gönderiye dair hiçbir mesaj gelmez.
      await izmPage.waitForTimeout(2_500);
      await expect(izmPage.getByRole("link", { name: shipment.trackingNumber })).toHaveCount(0);
      expect(izmMessages.of("ShipmentChanged", shipment.id)).toEqual([]);
      expect(izmMessages.raw.join("\n")).not.toContain(shipment.id);
      // Bağlantı gerçekten kurulmuştu (mesaj yokluğu bağlantı yokluğundan değil).
      expect(izmMessages.sockets.opened).toBeGreaterThan(0);
      expect(izmMessages.sockets.closed).toBeLessThan(izmMessages.sockets.opened);
    } finally {
      await ops.dispose();
      await istContext.close();
      await izmContext.close();
    }
  });

  test("kopukken yapılan değişiklik mesajla gelmez; yeniden bağlanınca yetkili HTTP eşitlemesiyle görünür", async ({ page }) => {
    const ops = await ApiSession.login("ops.agent");
    const hub = await ApiSession.login("hub.ist");
    const sockets: WebSocketRoute[] = [];
    let blockNegotiate = false;
    try {
      const shipment = await ops.createShipment({ externalReference: runTag("RECONNECT") });
      // Bağlantı sunucuya aynen aktarılır; test yalnız istediği anda koparır ve yeniden el sıkışmayı engeller.
      await page.routeWebSocket(/\/realtime\/operations/, (socket) => {
        sockets.push(socket);
        socket.connectToServer();
      });
      await page.route("**/realtime/operations/negotiate**", (route) => (blockNegotiate ? route.abort("connectionfailed") : route.continue()));

      await loginViaUi(page, "ops.agent");
      await page.goto(`/shipments/${shipment.id}`);
      const badge = page.locator(".detail-head .status");
      await expect(badge).toHaveText("Kaydedildi");
      await expect(statusBar(page)).toHaveAttribute("data-connection", "connected");

      // 1) Bağlantı kopar ve yeniden kurulamaz: arayüz bunu ve verinin bayat olabileceğini söyler.
      blockNegotiate = true;
      for (const socket of sockets) await socket.close();
      await expect(statusBar(page)).toHaveAttribute("data-connection", /reconnecting|disconnected/);
      await expect(statusBar(page)).toContainText("Görünen veri bayat olabilir");
      await expect(statusBar(page)).not.toContainText("Canlı güncelleme açık");

      // 2) Kopukken DB değişir. Mesaj gelemez; ekran (60 sn'lik mutabakata kadar) eski durumu gösterir.
      await hub.scan("IST-01", shipment.packages[0]!.labelCode, "Arrival");
      expect((await ops.detail(shipment.id)).body.status).toBe("AtHub");
      await page.waitForTimeout(3_000);
      await expect(badge).toHaveText("Kaydedildi");

      // 3) Bağlantı geri gelir: istemci kaçan mesajı beklemez, etkin sorguları sunucudan yeniden okur.
      blockNegotiate = false;
      await expect(statusBar(page)).toHaveAttribute("data-connection", "connected", { timeout: 30_000 });
      await expect(badge).toHaveText("Hub'da", { timeout: 8_000 });
      await expect(page.locator(".timeline li").first()).toContainText("Hub'a ulaştı");
      await expect(statusBar(page)).toContainText("Canlı güncelleme açık");

      // 4) Yeniden bağlandıktan sonra itme yine çalışır (abonelikler yenilendi).
      await hub.scan("IST-01", shipment.packages[0]!.labelCode, "Departure");
      await expect(badge).toHaveText("Yolda", { timeout: 8_000 });
    } finally {
      await ops.dispose();
      await hub.dispose();
    }
  });

  test("simüle carrier olayları gerçek hat üzerinden (webhook → inbox → outbox → SignalR) detayı canlı günceller; çıkışta bağlantı kapanır", async ({ page }) => {
    const manager = await ApiSession.login("ops.manager");
    const hubMessages = collectHubMessages(page);
    try {
      const shipment = await manager.createShipment({ externalReference: runTag("SIM") });
      const current = await manager.detail(shipment.id);
      const assigned = await manager.post(`/api/shipments/${shipment.id}/carrier`, { data: { carrierCode: "SIMEX" }, ifMatch: current.etag });
      expect(assigned.status()).toBe(200);

      await loginViaUi(page, "ops.manager");
      await page.goto(`/shipments/${shipment.id}`);
      await expect(statusBar(page)).toHaveAttribute("data-connection", "connected");
      await expect(page.locator(".detail-head .status")).toHaveText("Kaydedildi");

      const simulator = page.getByRole("region", { name: "Carrier simülasyonu" });
      await expect(simulator.getByText("Demo")).toBeVisible();
      await simulator.getByLabel("Senaryo").selectOption("HappyPath");
      await simulator.getByRole("button", { name: "Simüle olayları gönder" }).click();
      await expect(simulator.getByText(/olay gönderildi/)).toBeVisible();

      // Olaylar asenkron işlenir; son durum sayfa yenilenmeden görünür.
      await expect(page.locator(".detail-head .status")).toHaveText("Teslim edildi", { timeout: 20_000 });
      await expect(page.locator(".timeline li").first()).toContainText("Teslim edildi");
      await expect(page.locator(".timeline")).toContainText("Carrier");
      const statuses = hubMessages.of("ShipmentChanged", shipment.id).map((message) => message.arguments?.[0]?.status);
      expect(statuses).toContain("Delivered");

      // Çıkış: canlı bağlantı kapanır (istemci durdurur, sunucu da kullanıcının bağlantılarını keser).
      expect(hubMessages.sockets.currentOpen).toBe(true);
      await logoutViaUi(page);
      await expect.poll(() => hubMessages.sockets.currentOpen).toBe(false);
      await expect(statusBar(page)).toHaveCount(0);
    } finally {
      await manager.dispose();
    }
  });

  test("oturum cookie'si geçersizleşince arayüz giriş ekranına döner ve canlı bağlantı kapanır", async ({ page, context }) => {
    const hubMessages = collectHubMessages(page);
    await loginViaUi(page, "ops.agent");
    await page.goto("/shipments");
    await expect(statusBar(page)).toHaveAttribute("data-connection", "connected");
    await expect(page.getByRole("button", { name: "Yenile" })).toBeEnabled();
    expect(hubMessages.sockets.currentOpen).toBe(true);

    await context.clearCookies({ name: "__Host-lst.auth" });
    await page.getByRole("button", { name: "Yenile" }).click();

    await expect(page).toHaveURL(/\/login$/);
    await expect(page.getByText("Oturumunuz sona erdi. Devam etmek için yeniden giriş yapın.")).toBeVisible();
    await expect.poll(() => hubMessages.sockets.currentOpen).toBe(false);
  });
});
