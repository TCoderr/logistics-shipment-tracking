import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { ExceptionItem } from "../../api/types";
import { mockApi, problem, renderWithSession, resetHttpState, userFixture, type MockApi } from "../../test/harness";
import { ExceptionsPage } from "./ExceptionsPage";

const me = userFixture(["OpsAgent"]);

function item(overrides: Partial<ExceptionItem> = {}): ExceptionItem {
  return {
    id: "0192f9b0-0000-7000-8000-000000000001",
    type: "MaxAttemptsReached",
    severity: "High",
    status: "Open",
    shipment: { id: "0192f3a4-7c1e-7b2a-9c11-2f4e5d6a7b8c", trackingNumber: "LS4829105735", status: "DeliveryFailed" },
    hubCode: "ANK-02",
    occurrenceCount: 3,
    openedAt: "2026-10-10T07:00:00Z",
    dueAt: "2020-01-01T00:00:00Z",
    isOverdue: true,
    assignedTo: null,
    resolvedAt: null,
    resolutionCode: null,
    allowedResolutions: ["ReattemptApproved", "ReturnToSenderInitiated"],
    etag: '"100"',
    ...overrides,
  };
}

describe("istisna kuyruğu", () => {
  let api: MockApi;

  beforeEach(() => {
    resetHttpState();
    api = mockApi();
    api.on("GET", "/api/reference/hubs", { body: { items: [] } });
  });
  afterEach(() => vi.unstubAllGlobals());

  it("görünümü ve filtreleri sorguya çevirir; sahipsiz istisnada çözüm formu yerine üzerine alma ister", async () => {
    const user = userEvent.setup();
    api.on("GET", /^\/api\/exceptions/, { body: { items: [item()], nextCursor: null, limit: 50 } });
    renderWithSession(<ExceptionsPage />, { api, roles: ["OpsAgent"], route: "/exceptions?tab=unassigned&shipmentId=0192f3a4-7c1e-7b2a-9c11-2f4e5d6a7b8c" });

    expect(await screen.findByRole("link", { name: "LS4829105735" })).toBeInTheDocument();
    expect(screen.getByRole("cell", { name: /Deneme hakkı bitti/ })).toBeInTheDocument();
    expect(api.callsTo("GET", /^\/api\/exceptions/)[0]?.path).toBe(
      "/api/exceptions?assignedTo=unassigned&shipmentId=0192f3a4-7c1e-7b2a-9c11-2f4e5d6a7b8c&limit=50",
    );
    expect(screen.getByText("×3")).toBeInTheDocument();
    expect(screen.getByText(/geçti/)).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: /^Çöz:/ }));
    expect(screen.getByText(/Çözebilmek için istisnanın sizde olması gerekir/)).toBeInTheDocument();
    expect(screen.queryByLabelText("Çözüm notu")).not.toBeInTheDocument();
  });

  it("üzerine alırken If-Match ve kendi kimliğini gönderir, sonra kuyruğu yeniden okur", async () => {
    const user = userEvent.setup();
    let assigned = false;
    api.on("GET", /^\/api\/exceptions/, () => ({
      body: { items: [assigned ? item({ status: "Assigned", assignedTo: { id: me.id, displayName: me.displayName }, etag: '"101"' }) : item()], nextCursor: null, limit: 50 },
    }));
    api.on("POST", /\/assign$/, () => {
      assigned = true;
      return { body: item({ status: "Assigned" }), headers: { ETag: '"101"' } };
    });
    renderWithSession(<ExceptionsPage />, { api, roles: ["OpsAgent"], route: "/exceptions" });

    await user.click(await screen.findByRole("button", { name: /^Üzerime al/ }));

    expect(await screen.findByText("İstisna üzerinize alındı.")).toBeInTheDocument();
    const call = api.callsTo("POST", /\/assign$/)[0];
    expect(call?.headers["If-Match"]).toBe('"100"');
    expect(call?.body).toEqual({ assigneeUserId: me.id });
    await waitFor(() => expect(screen.getByText("Siz")).toBeInTheDocument());
    expect(screen.queryByRole("button", { name: /^Üzerime al/ })).not.toBeInTheDocument();
  });

  it("çözümde yalnız tipe izinli kodları sunar; 412'de kuyruğu yeniler, uyarır ve yazılan notu korur", async () => {
    const user = userEvent.setup();
    const mine = item({ status: "Assigned", assignedTo: { id: me.id, displayName: me.displayName } });
    let etag = '"100"';
    api.on("GET", /^\/api\/exceptions/, () => ({ body: { items: [{ ...mine, etag }], nextCursor: null, limit: 50 } }));
    api.on("POST", /\/resolve$/, () => {
      etag = '"102"';
      return { ...problem(412, "precondition-failed"), headers: { ETag: '"102"' } };
    });
    renderWithSession(<ExceptionsPage />, { api, roles: ["OpsAgent"], route: "/exceptions?tab=mine" });

    await user.click(await screen.findByRole("button", { name: /^Çöz:/ }));
    const options = Array.from(screen.getByLabelText<HTMLSelectElement>("Çözüm").options).map((option) => option.value);
    expect(options).toEqual(["", "ReattemptApproved", "ReturnToSenderInitiated"]);

    await user.click(screen.getByRole("button", { name: "Çözümü kaydet" }));
    expect(await screen.findByText("Bir çözüm seçin.")).toBeInTheDocument();
    expect(api.callsTo("POST", /\/resolve$/)).toHaveLength(0);

    await user.selectOptions(screen.getByLabelText("Çözüm"), "ReattemptApproved");
    await user.type(screen.getByLabelText("Çözüm notu"), "Alıcı arandı, yarın evde");
    await user.click(screen.getByRole("button", { name: "Çözümü kaydet" }));

    expect(await screen.findByText(/Bu istisna siz bakarken güncellendi/)).toBeInTheDocument();
    expect(api.callsTo("POST", /\/resolve$/)[0]?.headers["If-Match"]).toBe('"100"');
    expect(api.callsTo("POST", /\/resolve$/)[0]?.body).toEqual({ resolutionCode: "ReattemptApproved", note: "Alıcı arandı, yarın evde" });
    await waitFor(() => expect(api.callsTo("GET", /^\/api\/exceptions\?/).length).toBeGreaterThan(1));
    expect(screen.getByLabelText("Çözüm notu")).toHaveValue("Alıcı arandı, yarın evde");
    expect(screen.getByLabelText("Çözüm")).toHaveValue("ReattemptApproved");
  });

  it("boş kuyrukta görünüme özgü boş durum mesajı gösterir", async () => {
    api.on("GET", /^\/api\/exceptions/, { body: { items: [], nextCursor: null, limit: 50 } });
    renderWithSession(<ExceptionsPage />, { api, roles: ["OpsAgent"], route: "/exceptions?tab=mine" });

    expect(await screen.findByText("Üzerinizde açık istisna yok.")).toBeInTheDocument();
  });
});
