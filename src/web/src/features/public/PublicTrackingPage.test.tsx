import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi, type MockInstance } from "vitest";
import { mockApi, problem, resetHttpState, type MockApi } from "../../test/harness";
import { PublicTrackingPage } from "./PublicTrackingPage";

const code = "8M3QZ0V7K2XG9TJ4WD1R6HNB5C";
const view = {
  trackingNumberMasked: "LS48******35",
  status: "OutForDelivery",
  statusLabel: "Dağıtımda",
  promisedDeliveryDate: "2026-10-12",
  isDelayed: false,
  packageCount: 2,
  lastUpdatedAt: "2026-10-12T06:58:00Z",
  destinationCity: "Ankara",
  timeline: [
    { occurredAt: "2026-10-09T11:20:31Z", label: "Gönderi kaydı oluşturuldu", city: null },
    { occurredAt: "2026-10-12T06:58:00Z", label: "Dağıtıma çıktı", city: "Ankara" },
  ],
};

function renderPage() {
  const queryClient = new QueryClient();
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[`/t/${code}`]}>
        <Routes>
          <Route path="/t/:code" element={<PublicTrackingPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe("public takip sayfası", () => {
  let api: MockApi;
  let consoleLog: MockInstance;
  let consoleError: MockInstance;

  beforeEach(() => {
    resetHttpState();
    api = mockApi();
    consoleLog = vi.spyOn(globalThis.console, "log");
    consoleError = vi.spyOn(globalThis.console, "error");
  });
  afterEach(() => vi.unstubAllGlobals());

  it("yalnız allowlist alanlarını gösterir; oturum/CSRF çağrısı yapmaz; kodu başlığa, depolamaya veya console'a yazmaz", async () => {
    api.on("GET", `/api/public/tracking/${code}`, { body: view });
    const { container } = renderPage();

    expect(await screen.findByText("Dağıtımda")).toBeInTheDocument();
    expect(screen.getByText("LS48******35")).toBeInTheDocument();
    expect(screen.getByText(/12 Ekim Pazartesi/)).toBeInTheDocument();
    expect(screen.getByText(/Ad, adres ve telefon bilgileri bu sayfada gösterilmez/)).toBeInTheDocument();
    // En yeni olay üstte.
    const events = screen.getAllByRole("listitem").map((item) => item.textContent);
    expect(events[0]).toContain("Dağıtıma çıktı · Ankara");
    expect(events[1]).toContain("Gönderi kaydı oluşturuldu");

    expect(api.calls.map((call) => `${call.method} ${call.path}`)).toEqual([`GET /api/public/tracking/${code}`]);
    expect(api.calls[0]?.headers["X-CSRF-TOKEN"]).toBeUndefined();
    expect(document.title).toBe("Gönderi takibi");
    expect(container.innerHTML).not.toContain(code);
    expect(container.querySelector("a")).toBeNull();
    expect(JSON.stringify({ ...window.localStorage })).not.toContain(code);
    expect(JSON.stringify({ ...window.sessionStorage })).not.toContain(code);
    expect(consoleLog).not.toHaveBeenCalled();
    expect(consoleError).not.toHaveBeenCalled();
  });

  it("bulunamayan ve geçersiz kod için aynı nötr mesajı gösterir", async () => {
    api.on("GET", `/api/public/tracking/${code}`, { status: 404, body: { type: "urn:lst:problem:not-found", title: "Gönderi bulunamadı", status: 404, code: "not-found" } });
    renderPage();

    expect(await screen.findByText(/Gönderi bulunamadı\. Bağlantıyı kontrol edin/)).toBeInTheDocument();
    expect(screen.queryByText("Dağıtımda")).not.toBeInTheDocument();
  });

  it("rate limit yanıtında bekleme mesajı ve yeniden dene sunar", async () => {
    api.on("GET", `/api/public/tracking/${code}`, { ...problem(429, "rate-limited"), headers: { "Retry-After": "30" } });
    renderPage();

    expect(await screen.findByText(/Çok fazla istek gönderildi/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Yeniden dene" })).toBeInTheDocument();
  });
});
