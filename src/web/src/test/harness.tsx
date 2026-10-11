import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render } from "@testing-library/react";
import type { ReactElement } from "react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { vi } from "vitest";
import { setCsrfToken, setUnauthenticatedHandler } from "../api/http";
import type { Role } from "../api/types";
import { AuthProvider } from "../auth/AuthContext";

export interface RecordedCall {
  method: string;
  path: string;
  headers: Record<string, string>;
  body: unknown;
}

export interface ResponseSpec {
  status?: number;
  body?: unknown;
  headers?: Record<string, string>;
  /** true: fetch reddedilir (yanıt alınamadı). */
  networkError?: boolean;
}

type Responder = (call: RecordedCall) => ResponseSpec | Promise<ResponseSpec>;

interface RouteSpec {
  method: string;
  match: string | RegExp;
  respond: Responder;
}

/** `fetch` yerine geçen küçük yönlendirici. Eşleşmeyen istek testi kırar (gizli çağrı kalmasın). */
export function mockApi() {
  const routes: RouteSpec[] = [];
  const calls: RecordedCall[] = [];

  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    const url = typeof input === "string" ? input : input instanceof URL ? input.toString() : input.url;
    const method = (init?.method ?? "GET").toUpperCase();
    const headers = (init?.headers ?? {}) as Record<string, string>;
    const body: unknown = typeof init?.body === "string" ? JSON.parse(init.body) : undefined;
    const call: RecordedCall = { method, path: url, headers, body };
    calls.push(call);
    const route = routes.find((candidate) => candidate.method === method && (typeof candidate.match === "string" ? candidate.match === url : candidate.match.test(url)));
    if (!route) throw new Error(`Beklenmeyen istek: ${method} ${url}`);
    const spec = await route.respond(call);
    if (spec.networkError) throw new TypeError("Failed to fetch");
    const status = spec.status ?? 200;
    const hasBody = spec.body !== undefined && status !== 204;
    return new Response(hasBody ? JSON.stringify(spec.body) : null, {
      status,
      headers: { ...(hasBody ? { "Content-Type": status >= 400 ? "application/problem+json" : "application/json" } : {}), ...spec.headers },
    });
  });
  vi.stubGlobal("fetch", fetchMock);

  const api = {
    calls,
    on(method: string, match: string | RegExp, respond: Responder | ResponseSpec) {
      // Sonradan eklenen kural önceliklidir (test içinde davranış değiştirmek için).
      routes.unshift({ method, match, respond: typeof respond === "function" ? respond : () => respond });
      return api;
    },
    callsTo(method: string, match: string | RegExp): RecordedCall[] {
      return calls.filter((call) => call.method === method && (typeof match === "string" ? call.path === match : match.test(call.path)));
    },
  };
  return api;
}

export type MockApi = ReturnType<typeof mockApi>;

export function userFixture(roles: Role[], hubCodes: string[] = []) {
  return { id: "11111111-1111-4111-8111-111111111111", displayName: "Test Kullanıcı (sentetik)", roles, hubCodes, csrfToken: "csrf-user-token" };
}

export function problem(status: number, code: string, extra: Record<string, unknown> = {}): ResponseSpec {
  return { status, body: { type: `urn:lst:problem:${code}`, title: code, status, code, traceId: "00-test-trace-01", ...extra } };
}

/** Oturumlu kullanıcıyla, gerçek AuthProvider + yönlendirici + sorgu istemcisi içinde render eder. */
export function renderWithSession(ui: ReactElement, options: { api: MockApi; roles: Role[]; hubCodes?: string[]; route?: string; path?: string }) {
  const user = userFixture(options.roles, options.hubCodes);
  options.api.on("GET", "/api/auth/me", { body: user });
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 }, mutations: { retry: false } } });
  const view = render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[options.route ?? "/"]}>
        <AuthProvider>
          <SessionGate>
            <Routes>
              <Route path={options.path ?? "*"} element={ui} />
              <Route path="*" element={<p>başka sayfa</p>} />
            </Routes>
          </SessionGate>
        </AuthProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  );
  return { ...view, user, queryClient };
}

import { useAuth } from "../auth/AuthContext";

function SessionGate({ children }: { children: ReactElement }) {
  const { state } = useAuth();
  if (state.status === "loading") return <p>oturum yükleniyor</p>;
  if (state.status === "anonymous") return <p>oturum yok</p>;
  return children;
}

export function resetHttpState(): void {
  setCsrfToken(null);
  setUnauthenticatedHandler(null);
}
