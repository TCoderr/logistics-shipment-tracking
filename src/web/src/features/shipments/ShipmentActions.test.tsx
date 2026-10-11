import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { setCsrfToken } from "../../api/http";
import type { AllowedAction, ShipmentDetail } from "../../api/types";
import type { SessionUser } from "../../auth/AuthContext";
import { mockApi, problem, resetHttpState, type MockApi } from "../../test/harness";
import { ShipmentActions } from "./ShipmentActions";
import { groupTimeline } from "./Timeline";

const shipmentId = "0192f3a4-7c1e-7b2a-9c11-2f4e5d6a7b8c";

function detail(status: ShipmentDetail["status"], allowedActions: AllowedAction[], extra: Partial<ShipmentDetail> = {}): ShipmentDetail {
  return {
    id: shipmentId,
    trackingNumber: "LS4829105735",
    externalReference: null,
    kind: "Forward",
    serviceLevel: "Standard",
    status,
    statusOccurredAt: "2026-10-10T09:00:00Z",
    statusChangedAt: "2026-10-10T09:00:05Z",
    isDelayed: false,
    promisedDeliveryDate: "2026-10-12",
    deliveredAt: null,
    originHub: { code: "IST-01", name: "İstanbul", city: "İstanbul" },
    destinationHub: { code: "ANK-02", name: "Ankara", city: "Ankara" },
    currentHub: null,
    carrier: null,
    deliveryAttempts: { count: 0, max: 3, attempts: [] },
    declaredValue: null,
    sender: { fullName: "S", companyName: null, phone: null, email: null, addressLine1: null, addressLine2: null, district: null, city: "İstanbul", postalCode: null, countryCode: "TR" },
    recipient: { fullName: "R", companyName: null, phone: null, email: null, addressLine1: null, addressLine2: null, district: null, city: "Ankara", postalCode: null, countryCode: "TR" },
    packages: [],
    timeline: [],
    openExceptions: [],
    allowedActions,
    createdAt: "2026-10-09T11:20:31Z",
    publicTrackingCode: null,
    piiRedacted: false,
    ...extra,
  };
}

function sessionUser(kind: "agent" | "manager" | "hub"): SessionUser {
  return {
    id: "u1",
    displayName: "Test",
    roles: kind === "agent" ? ["OpsAgent"] : kind === "manager" ? ["OpsManager"] : ["HubOperator"],
    hubCodes: kind === "hub" ? ["ANK-02"] : [],
    isOps: kind !== "hub",
    isManager: kind === "manager",
    isHubOperator: kind === "hub",
  };
}

function renderActions(props: { detail: ShipmentDetail; user: SessionUser; refresh?: () => Promise<ShipmentDetail | undefined> }) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const refresh = props.refresh ?? vi.fn(() => Promise.resolve(props.detail));
  const view = render(
    <QueryClientProvider client={queryClient}>
      <ShipmentActions detail={props.detail} etag={'"41"'} user={props.user} refresh={refresh} />
    </QueryClientProvider>,
  );
  return { ...view, refresh, queryClient };
}

describe("gönderi işlemleri paneli", () => {
  let api: MockApi;

  beforeEach(() => {
    resetHttpState();
    setCsrfToken("csrf-user-token");
    api = mockApi();
    api.on("GET", "/api/reference/carriers", { body: { items: [{ code: "SIMEX", name: "SimEx Kargo", isActive: true }] } });
  });
  afterEach(() => vi.unstubAllGlobals());

  it("yalnız sunucunun bildirdiği izinli işlemleri gösterir ve neden iptal olmadığını açıklar", () => {
    renderActions({ detail: detail("InTransit", ["ReturnToSender"]), user: sessionUser("agent") });

    expect(screen.getByRole("button", { name: "Göndericiye iade başlat" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Gönderiyi iptal et" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Teslimat denemesi kaydet" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /Carrier/ })).not.toBeInTheDocument();
    expect(screen.getByText(/İptal yok: gönderi hub'dan çıkış yaptı/)).toBeInTheDocument();
  });

  it("hub görevlisine iptal/iade/carrier/kod yenileme göstermez; yalnız teslimat denemesi", () => {
    renderActions({ detail: detail("OutForDelivery", ["RecordDeliveryAttempt"]), user: sessionUser("hub") });

    expect(screen.getAllByRole("button").map((button) => button.textContent)).toEqual(["Teslimat denemesi kaydet"]);
  });

  it("kod yenilemeyi yalnız yöneticiye ve kod görünürken sunar", () => {
    const withCode = detail("Created", ["Cancel"], { publicTrackingCode: "8M3QZ0V7K2XG9TJ4WD1R6HNB5C" });
    const { unmount } = renderActions({ detail: withCode, user: sessionUser("agent") });
    expect(screen.queryByRole("button", { name: "Takip kodunu yenile" })).not.toBeInTheDocument();
    unmount();

    renderActions({ detail: withCode, user: sessionUser("manager") });
    expect(screen.getByRole("button", { name: "Takip kodunu yenile" })).toBeInTheDocument();
  });

  it("If-Match ile iptal eder; 412'de güncel durumu yükler, çakışmayı bildirir ve yazılan nedeni korur", async () => {
    const user = userEvent.setup();
    api.on("POST", `/api/shipments/${shipmentId}/cancel`, { ...problem(412, "precondition-failed"), headers: { ETag: '"42"' } });
    const fresh = detail("AtHub", ["Cancel"], { statusChangedAt: "2026-10-10T11:03:00Z" });
    const refresh = vi.fn(() => Promise.resolve(fresh));
    renderActions({ detail: detail("Created", ["Cancel"]), user: sessionUser("agent"), refresh });

    await user.click(screen.getByRole("button", { name: "Gönderiyi iptal et" }));
    await user.type(screen.getByLabelText("İptal nedeni"), "Gönderici vazgeçti");
    await user.click(screen.getAllByRole("button", { name: "Gönderiyi iptal et" })[1]!);

    expect(await screen.findByText(/Bu gönderi siz bakarken güncellendi \(Hub'da, 14:03\)/)).toBeInTheDocument();
    expect(refresh).toHaveBeenCalledTimes(1);
    const call = api.callsTo("POST", `/api/shipments/${shipmentId}/cancel`)[0];
    expect(call?.headers["If-Match"]).toBe('"41"');
    expect(call?.headers["X-CSRF-TOKEN"]).toBe("csrf-user-token");
    expect(call?.body).toEqual({ reason: "Gönderici vazgeçti" });
    expect(screen.getByLabelText("İptal nedeni")).toHaveValue("Gönderici vazgeçti");
  });

  it("409 reddinde sunucu ipucunu gösterir ve durumu yeniden okur", async () => {
    const user = userEvent.setup();
    api.on("POST", `/api/shipments/${shipmentId}/cancel`, problem(409, "shipment-not-cancellable", { currentStatus: "InTransit", returnToSenderAllowed: true }));
    const { refresh } = renderActions({ detail: detail("AtHub", ["Cancel"]), user: sessionUser("agent") });

    await user.click(screen.getByRole("button", { name: "Gönderiyi iptal et" }));
    await user.type(screen.getByLabelText("İptal nedeni"), "x");
    await user.click(screen.getAllByRole("button", { name: "Gönderiyi iptal et" })[1]!);

    expect(await screen.findByText("Gönderi bu durumda iptal edilemez.")).toBeInTheDocument();
    expect(screen.getByText("Bu gönderi için göndericiye iade başlatılabilir.")).toBeInTheDocument();
    expect(refresh).toHaveBeenCalled();
  });

  it("teslimat denemesinde yanıt kaybolursa aynı Idempotency-Key ile tekrar gönderir; başarısız sonuçta nedeni zorunlu tutar", async () => {
    const user = userEvent.setup();
    let attempt = 0;
    api.on("POST", `/api/shipments/${shipmentId}/delivery-attempts`, () =>
      ++attempt === 1 ? { networkError: true } : { status: 201, body: { attemptNumber: 1 }, headers: { ETag: '"50"' } },
    );
    renderActions({ detail: detail("OutForDelivery", ["RecordDeliveryAttempt"]), user: sessionUser("hub") });

    await user.click(screen.getByRole("button", { name: "Teslimat denemesi kaydet" }));
    await user.selectOptions(screen.getByLabelText("Sonuç"), "Failed");
    await user.selectOptions(screen.getByLabelText("Neden"), "Other");
    await user.click(screen.getByRole("button", { name: "Denemeyi kaydet" }));
    expect(await screen.findByText("'Diğer' nedeni için not zorunludur.")).toBeInTheDocument();
    expect(api.callsTo("POST", `/api/shipments/${shipmentId}/delivery-attempts`)).toHaveLength(0);

    await user.type(screen.getByLabelText("Not"), "Kapı kilitli");
    await user.click(screen.getByRole("button", { name: "Denemeyi kaydet" }));
    expect(await screen.findByText(/Yanıt alınamadı\. Kayıt sunucuya ulaşmış olabilir/)).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Denemeyi kaydet" }));
    await waitFor(() => expect(screen.getByText("Başarısız teslimat denemesi kaydedildi.")).toBeInTheDocument());
    const [first, second] = api.callsTo("POST", `/api/shipments/${shipmentId}/delivery-attempts`);
    expect(second?.headers["Idempotency-Key"]).toBe(first?.headers["Idempotency-Key"]);
    expect(second?.headers["If-Match"]).toBeUndefined();
    expect(second?.body).toMatchObject({ outcome: "Failed", failureReason: "Other", note: "Kapı kilitli" });
  });
});

describe("zaman çizelgesi gruplaması", () => {
  it("aynı hub'da art arda gelen aynı tip koli taramalarını tek satırda toplar, farklı sonuçları ayırır", () => {
    const base = { source: "HubScan" as const, receivedAt: "2026-10-09T16:02:03Z", statusAfter: null, reasonCode: null, isLate: false };
    const hub = { hubCode: "IST-01", city: "İstanbul" };
    const rows = groupTimeline([
      { ...base, id: 1, eventType: "Created", source: "Operator", outcome: "Applied", occurredAt: "2026-10-09T11:20:31Z", location: null },
      { ...base, id: 2, eventType: "ArrivedAtHub", outcome: "Applied", occurredAt: "2026-10-09T16:02:00Z", location: hub },
      { ...base, id: 3, eventType: "ArrivedAtHub", outcome: "Applied", occurredAt: "2026-10-09T16:02:10Z", location: hub },
      { ...base, id: 4, eventType: "ArrivedAtHub", outcome: "Stale", occurredAt: "2026-10-09T16:02:20Z", location: hub, isLate: true },
      { ...base, id: 5, eventType: "DepartedHub", outcome: "Applied", occurredAt: "2026-10-09T18:00:00Z", location: hub },
    ]);

    expect(rows.map((row) => [row.first.id, row.count])).toEqual([
      [1, 1],
      [2, 2],
      [4, 1],
      [5, 1],
    ]);
  });
});
