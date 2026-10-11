import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { mockApi, problem, resetHttpState, type MockApi } from "../test/harness";
import { ApiError, NetworkError, request, setCsrfToken, setUnauthenticatedHandler, toQuery } from "./http";

describe("API istemcisi", () => {
  let api: MockApi;

  beforeEach(() => {
    resetHttpState();
    api = mockApi();
  });
  afterEach(() => vi.unstubAllGlobals());

  it("POST öncesi anonim CSRF token'ı alır ve X-CSRF-TOKEN ile gönderir; GET'e token eklemez", async () => {
    api.on("GET", "/api/auth/csrf", { body: { csrfToken: "anon-token" } });
    api.on("POST", "/api/auth/login", { status: 204 });
    api.on("GET", "/api/shipments", { body: { items: [] } });

    await request("/api/auth/login", { method: "POST", body: { userName: "u", password: "p" } });
    await request("/api/shipments");

    expect(api.callsTo("POST", "/api/auth/login")[0]?.headers["X-CSRF-TOKEN"]).toBe("anon-token");
    expect(api.callsTo("GET", "/api/shipments")[0]?.headers["X-CSRF-TOKEN"]).toBeUndefined();
  });

  it("CSRF reddinde token'ı /me'den yeniler ve isteği yalnız bir kez tekrarlar", async () => {
    setCsrfToken("stale-token");
    let attempts = 0;
    api.on("GET", "/api/auth/me", { body: { csrfToken: "fresh-token" } });
    api.on("POST", "/api/shipments/x/cancel", () => (++attempts === 1 ? problem(400, "csrf-validation-failed") : { body: { ok: true } }));

    const result = await request<{ ok: boolean }>("/api/shipments/x/cancel", { method: "POST", body: { reason: "r" }, ifMatch: '"7"' });

    expect(result.data.ok).toBe(true);
    const posts = api.callsTo("POST", "/api/shipments/x/cancel");
    expect(posts.map((call) => call.headers["X-CSRF-TOKEN"])).toEqual(["stale-token", "fresh-token"]);
    expect(posts[1]?.headers["If-Match"]).toBe('"7"');
  });

  it("ikinci CSRF reddinde döngüye girmez", async () => {
    setCsrfToken("stale-token");
    api.on("GET", "/api/auth/me", { body: { csrfToken: "fresh-token" } });
    api.on("POST", "/api/x", problem(400, "csrf-validation-failed"));

    await expect(request("/api/x", { method: "POST" })).rejects.toMatchObject({ code: "csrf-validation-failed" });
    expect(api.callsTo("POST", "/api/x")).toHaveLength(2);
  });

  it("412 yanıtındaki güncel ETag'i ve problem uzantılarını hataya taşır", async () => {
    setCsrfToken("t");
    api.on("POST", "/api/x", { ...problem(412, "precondition-failed"), headers: { ETag: '"99"' } });
    api.on("POST", "/api/y", { ...problem(409, "shipment-not-cancellable", { currentStatus: "InTransit", returnToSenderAllowed: true }) });

    const conflict = await request("/api/x", { method: "POST" }).catch((error: unknown) => error);
    expect(conflict).toBeInstanceOf(ApiError);
    expect((conflict as ApiError).etag).toBe('"99"');

    const refused = (await request("/api/y", { method: "POST" }).catch((error: unknown) => error)) as ApiError;
    expect(refused.status).toBe(409);
    expect(refused.problem.returnToSenderAllowed).toBe(true);
    expect(refused.traceId).toBe("00-test-trace-01");
  });

  it("yanıt alınamayınca NetworkError üretir (sonuç bilinmiyor), Retry-After'ı okur", async () => {
    setCsrfToken("t");
    api.on("POST", "/api/lost", { networkError: true });
    api.on("GET", "/api/limited", { ...problem(429, "rate-limited"), headers: { "Retry-After": "12" } });

    await expect(request("/api/lost", { method: "POST" })).rejects.toBeInstanceOf(NetworkError);
    await expect(request("/api/limited")).rejects.toMatchObject({ status: 429, retryAfterSeconds: 12 });
  });

  it("401 unauthenticated yanıtında oturum bitti işleyicisini çağırır; public isteklerde çağırmaz ve CSRF/yönlendirme uygulamaz", async () => {
    const onExpired = vi.fn();
    setUnauthenticatedHandler(onExpired);
    api.on("GET", "/api/shipments", problem(401, "unauthenticated"));
    api.on("GET", "/api/public/tracking/ABC", problem(404, "not-found"));

    await expect(request("/api/shipments")).rejects.toMatchObject({ status: 401 });
    expect(onExpired).toHaveBeenCalledTimes(1);

    await expect(request("/api/public/tracking/ABC", { anonymous: true })).rejects.toMatchObject({ status: 404 });
    expect(onExpired).toHaveBeenCalledTimes(1);
    expect(api.callsTo("GET", "/api/public/tracking/ABC")[0]?.headers["X-CSRF-TOKEN"]).toBeUndefined();
  });

  it("idempotent yanıtın replay olduğunu bildirir", async () => {
    setCsrfToken("t");
    api.on("POST", "/api/shipments", { status: 201, body: { id: "1" }, headers: { "Idempotency-Replayed": "true", ETag: '"5"', Location: "/api/shipments/1" } });

    const result = await request<{ id: string }>("/api/shipments", { method: "POST", body: {}, idempotencyKey: "key-1234567890abcdef" });

    expect(result.replayed).toBe(true);
    expect(result.etag).toBe('"5"');
    expect(api.calls[0]?.headers["Idempotency-Key"]).toBe("key-1234567890abcdef");
  });

  it("boş filtreleri sorguya yazmaz (sunucu boş değeri hata sayar), dizileri tekrarlı parametre yapar", () => {
    expect(toQuery({ q: "", hubCode: undefined, delayed: false, cursor: null })).toBe("");
    expect(toQuery({ status: ["AtHub", "InTransit"], delayed: true, limit: 50 })).toBe("?status=AtHub&status=InTransit&delayed=true&limit=50");
  });
});
