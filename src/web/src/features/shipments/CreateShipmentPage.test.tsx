import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { mockApi, problem, renderWithSession, resetHttpState, type MockApi } from "../../test/harness";
import { CreateShipmentPage } from "./CreateShipmentPage";

const hubs = {
  items: [
    { code: "IST-01", name: "İstanbul Avrupa Aktarma", city: "İstanbul", isActive: true },
    { code: "ANK-02", name: "Ankara Çankaya Dağıtım", city: "Ankara", isActive: true },
    { code: "BRS-01", name: "Bursa Aktarma", city: "Bursa", isActive: false },
  ],
};
const created = {
  id: "0192f3a4-7c1e-7b2a-9c11-2f4e5d6a7b8c",
  trackingNumber: "LS4829105735",
  publicTrackingCode: "8M3QZ0V7K2XG9TJ4WD1R6HNB5C",
  status: "Created",
  serviceLevel: "Standard",
  promisedDeliveryDate: "2026-10-12",
  packages: [{ id: "p1", sequence: 1, labelCode: "LS4829105735-01" }],
  createdAt: "2026-10-09T11:20:31Z",
};

async function fillValidForm(user: ReturnType<typeof userEvent.setup>) {
  // Hub listesi sunucudan gelene kadar beklenir.
  await screen.findAllByRole("option", { name: /IST-01/ });
  await user.selectOptions(screen.getByLabelText("Çıkış hub'ı"), "IST-01");
  await user.selectOptions(screen.getByLabelText("Varış hub'ı"), "ANK-02");
  for (const [legend, name, phone, city] of [
    ["Gönderen", "Sentetik Gönderen", "+902120000001", "İstanbul"],
    ["Alıcı", "Sentetik Alıcı", "+905320000002", "Ankara"],
  ] as const) {
    const group = within(screen.getByRole("group", { name: legend }));
    await user.type(group.getByLabelText("Ad soyad"), name);
    await user.type(group.getByLabelText("Telefon"), phone);
    await user.type(group.getByLabelText("Adres satırı 1"), "Test Cd. No: 1");
    await user.type(group.getByLabelText("Şehir"), city);
    await user.type(group.getByLabelText("Posta kodu"), "06420");
  }
  await user.type(screen.getByLabelText("Koli 1 ağırlık (g)"), "2400");
  await user.type(screen.getByLabelText("Boy (mm)"), "400");
  await user.type(screen.getByLabelText("En (mm)"), "300");
  await user.type(screen.getByLabelText("Yükseklik (mm)"), "200");
}

describe("gönderi oluşturma formu", () => {
  let api: MockApi;

  beforeEach(() => {
    resetHttpState();
    api = mockApi();
    api.on("GET", "/api/reference/hubs", { body: hubs });
    api.on("GET", /^\/api\/shipments\/0192f3a4/, { body: {}, headers: { ETag: '"1"' } });
  });
  afterEach(() => vi.unstubAllGlobals());

  it("yalnız aktif hub'ları seçtirir", async () => {
    renderWithSession(<CreateShipmentPage />, { api, roles: ["OpsAgent"] });

    const origin = await screen.findByLabelText("Çıkış hub'ı");
    await waitFor(() => expect(within(origin).getAllByRole("option")).toHaveLength(3));
    expect(within(origin).queryByRole("option", { name: /BRS-01/ })).not.toBeInTheDocument();
  });

  it("yanıt kaybolunca aynı Idempotency-Key ile yeniden dener ve sonucu replay olarak bildirir", async () => {
    const user = userEvent.setup();
    let attempt = 0;
    api.on("POST", "/api/shipments", () =>
      ++attempt === 1 ? { networkError: true } : { status: 201, body: created, headers: { "Idempotency-Replayed": "true", ETag: '"1"' } },
    );
    renderWithSession(<CreateShipmentPage />, { api, roles: ["OpsAgent"], route: "/shipments/new" });
    await fillValidForm(user);

    await user.click(screen.getByRole("button", { name: "Gönderiyi oluştur" }));
    expect(await screen.findByText(/Yanıt alınamadı\. İstek sunucuya ulaşmış olabilir/)).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Aynı işlemi yeniden dene" }));
    await waitFor(() => expect(api.callsTo("POST", "/api/shipments")).toHaveLength(2));

    const [first, second] = api.callsTo("POST", "/api/shipments");
    expect(first?.headers["Idempotency-Key"]).toMatch(/^[A-Za-z0-9_-]{16,128}$/);
    expect(second?.headers["Idempotency-Key"]).toBe(first?.headers["Idempotency-Key"]);
    expect(second?.body).toEqual(first?.body);
    expect(second?.headers["X-CSRF-TOKEN"]).toBe("csrf-user-token");
  });

  it("yanıt kaybından sonra alan değişirse uyarır ve YENİ anahtarla gönderir", async () => {
    const user = userEvent.setup();
    let attempt = 0;
    api.on("POST", "/api/shipments", () => (++attempt === 1 ? { networkError: true } : { status: 201, body: created, headers: { ETag: '"1"' } }));
    renderWithSession(<CreateShipmentPage />, { api, roles: ["OpsAgent"], route: "/shipments/new" });
    await fillValidForm(user);
    await user.click(screen.getByRole("button", { name: "Gönderiyi oluştur" }));
    await screen.findByRole("button", { name: "Aynı işlemi yeniden dene" });

    await user.type(screen.getByLabelText("Sipariş referansı (isteğe bağlı)"), "ORD-TEST-1");

    expect(screen.getByText("Alanları değiştirdiniz.")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Yeni işlem olarak gönder" }));
    await waitFor(() => expect(api.callsTo("POST", "/api/shipments")).toHaveLength(2));
    const [first, second] = api.callsTo("POST", "/api/shipments");
    expect(second?.headers["Idempotency-Key"]).not.toBe(first?.headers["Idempotency-Key"]);
    expect((second?.body as { externalReference: string }).externalReference).toBe("ORD-TEST-1");
  });

  it("sunucu doğrulama hatalarını ilgili alanda gösterir (kural ve JSON yolu anahtarları)", async () => {
    const user = userEvent.setup();
    api.on(
      "POST",
      "/api/shipments",
      problem(400, "validation", { errors: { "recipient.phone": ["Telefon E.164 biçiminde olmalı."], "$.packages[0].weightGrams": ["1 ile 70000 arasında olmalı."] } }),
    );
    renderWithSession(<CreateShipmentPage />, { api, roles: ["OpsAgent"], route: "/shipments/new" });
    await fillValidForm(user);

    await user.click(screen.getByRole("button", { name: "Gönderiyi oluştur" }));

    const recipient = within(screen.getByRole("group", { name: "Alıcı" }));
    await waitFor(() => expect(recipient.getByLabelText("Telefon")).toHaveAccessibleDescription(/E\.164/));
    expect(recipient.getByLabelText("Telefon")).toBeInvalid();
    expect(screen.getByLabelText("Koli 1 ağırlık (g)")).toHaveAccessibleDescription(/1 ile 70000/);
    expect(screen.getByText(/2 alan düzeltilmeli/)).toBeInTheDocument();
  });

  it("eksik zorunlu alanla sunucuya istek göndermez", async () => {
    const user = userEvent.setup();
    renderWithSession(<CreateShipmentPage />, { api, roles: ["OpsAgent"], route: "/shipments/new" });
    await screen.findByLabelText("Çıkış hub'ı");

    await user.click(screen.getByRole("button", { name: "Gönderiyi oluştur" }));

    expect(await screen.findByText(/alan düzeltilmeli/)).toBeInTheDocument();
    expect(api.callsTo("POST", "/api/shipments")).toHaveLength(0);
  });
});
