import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { mockApi, problem, renderWithSession, resetHttpState, type MockApi } from "../../test/harness";
import { ScanPage } from "./ScanPage";

const views = {
  shipment: { id: "0192f3a4-7c1e-7b2a-9c11-2f4e5d6a7b8c", trackingNumber: "LS4829105735", status: "AtHub", currentHubCode: "IST-01" },
  package: { labelCode: "LS4829105735-01", currentHubCode: "IST-01", lastScanType: "Arrival" },
  consolidation: { packagesAtHub: 1, packageCount: 2 },
};

describe("hub tarama ekranı", () => {
  let api: MockApi;

  beforeEach(() => {
    resetHttpState();
    api = mockApi();
  });
  afterEach(() => vi.unstubAllGlobals());

  it("uygulanan taramayı sesli okunur sonuç kutusunda durum ve konsolidasyonla gösterir", async () => {
    const user = userEvent.setup();
    api.on("POST", "/api/hubs/IST-01/scans", { body: { outcome: "Applied", reasonCode: null, ...views } });
    renderWithSession(<ScanPage />, { api, roles: ["HubOperator"], hubCodes: ["IST-01"] });

    await user.type(await screen.findByLabelText("Koli etiketi"), "ls4829105735-01");
    await user.click(screen.getByRole("button", { name: "Taramayı kaydet" }));

    // Sonuç hem sesli okunan kutuda hem oturum geçmişinde görünür.
    const results = await screen.findAllByText("Uygulandı");
    expect(results.some((element) => element.closest("[aria-live='assertive']") !== null)).toBe(true);
    expect(screen.getByText(/Hub'da · 1\/2 koli bu hub'da/)).toBeInTheDocument();
    const call = api.callsTo("POST", "/api/hubs/IST-01/scans")[0];
    expect(call?.body).toMatchObject({ labelCode: "LS4829105735-01", scanType: "Arrival" });
    expect((call?.body as { clientScanId: string }).clientScanId).toMatch(/^[A-Za-z0-9_.:-]{1,64}$/);
    expect((call?.body as { occurredAt: string }).occurredAt).toMatch(/Z$/);
    expect(screen.getByLabelText("Koli etiketi")).toHaveValue("");
  });

  it("iş kuralı reddini nedeni ve koli sayısıyla gösterir", async () => {
    const user = userEvent.setup();
    api.on("POST", "/api/hubs/IST-01/scans", problem(409, "packages-not-consolidated", { outcome: "Rejected", reasonCode: "packages-not-consolidated", ...views }));
    renderWithSession(<ScanPage />, { api, roles: ["HubOperator"], hubCodes: ["IST-01"] });

    await user.selectOptions(await screen.findByLabelText("Tarama tipi"), "Departure");
    await user.type(screen.getByLabelText("Koli etiketi"), "LS4829105735-01");
    await user.click(screen.getByRole("button", { name: "Taramayı kaydet" }));

    expect((await screen.findAllByText("Reddedildi · Kolilerin hepsi bu hub'da değil")).length).toBeGreaterThan(0);
    expect(screen.getByText(/1\/2 koli bu hub'da/)).toBeInTheDocument();
  });

  it("yanıt kaybında aynı tarama kimliği ve zamanıyla yeniden gönderir (çift kayıt oluşmaz)", async () => {
    const user = userEvent.setup();
    let attempt = 0;
    api.on("POST", "/api/hubs/IST-01/scans", () =>
      ++attempt === 1 ? { networkError: true } : { body: { outcome: "Duplicate", reasonCode: "technical-duplicate", ...views } },
    );
    renderWithSession(<ScanPage />, { api, roles: ["HubOperator"], hubCodes: ["IST-01"] });

    await user.type(await screen.findByLabelText("Koli etiketi"), "LS4829105735-01");
    await user.click(screen.getByRole("button", { name: "Taramayı kaydet" }));
    expect((await screen.findAllByText("Yanıt alınamadı")).length).toBeGreaterThan(0);
    expect(screen.getByLabelText("Koli etiketi")).toHaveValue("LS4829105735-01");

    await user.click(screen.getByRole("button", { name: "Taramayı kaydet" }));
    await waitFor(() => expect(api.callsTo("POST", "/api/hubs/IST-01/scans")).toHaveLength(2));
    const [first, second] = api.callsTo("POST", "/api/hubs/IST-01/scans");
    expect(second?.body).toEqual(first?.body);
    expect((await screen.findAllByText("Tekrar · daha önce kaydedildi")).length).toBeGreaterThan(0);
  });

  it("biçimi bozuk etiketi sunucuya göndermez; hub ataması olmayan kullanıcıya tarama formu göstermez", async () => {
    const user = userEvent.setup();
    const { unmount } = renderWithSession(<ScanPage />, { api, roles: ["HubOperator"], hubCodes: ["IST-01"] });
    await user.type(await screen.findByLabelText("Koli etiketi"), "ABC");
    await user.click(screen.getByRole("button", { name: "Taramayı kaydet" }));
    expect(await screen.findByText(/Koli etiketi LS \+ 10 rakam/)).toBeInTheDocument();
    expect(api.callsTo("POST", /scans/)).toHaveLength(0);
    unmount();

    renderWithSession(<ScanPage />, { api, roles: ["HubOperator"], hubCodes: [] });
    expect(await screen.findByText(/Size atanmış bir hub yok/)).toBeInTheDocument();
    expect(screen.queryByLabelText("Koli etiketi")).not.toBeInTheDocument();
  });
});
