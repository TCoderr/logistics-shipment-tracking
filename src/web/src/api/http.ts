// Aynı origin API istemcisi. Cookie'ler tarayıcıda (HttpOnly) kalır; CSRF token'ı yalnız bellekte tutulur.
// Hiçbir istek/yanıt içeriği, token veya kod console'a ya da kalıcı depoya yazılmaz.

export interface ProblemBody {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  code?: string;
  traceId?: string;
  errors?: Record<string, string[]>;
  [extension: string]: unknown;
}

/** Sunucunun ProblemDetails ile yanıtladığı hata. */
export class ApiError extends Error {
  readonly status: number;
  readonly code: string;
  readonly problem: ProblemBody;
  /** 412 yanıtlarında sunucunun bildirdiği güncel ETag. */
  readonly etag: string | null;
  readonly retryAfterSeconds: number | null;

  constructor(status: number, problem: ProblemBody, etag: string | null, retryAfterSeconds: number | null) {
    super(problem.title ?? `HTTP ${status}`);
    this.name = "ApiError";
    this.status = status;
    this.code = problem.code ?? `http-${status}`;
    this.problem = problem;
    this.etag = etag;
    this.retryAfterSeconds = retryAfterSeconds;
  }

  get traceId(): string | undefined {
    return this.problem.traceId;
  }

  get fieldErrors(): Record<string, string[]> {
    return this.problem.errors ?? {};
  }
}

/** Yanıt alınamadı: istek sunucuya ulaşmış ve işlenmiş OLABİLİR (sonuç bilinmiyor). */
export class NetworkError extends Error {
  constructor() {
    super("Sunucuya ulaşılamadı veya yanıt alınamadı.");
    this.name = "NetworkError";
  }
}

export interface ApiResult<T> {
  data: T;
  status: number;
  etag: string | null;
  location: string | null;
  replayed: boolean;
}

export interface RequestOptions {
  method?: "GET" | "POST";
  body?: unknown;
  ifMatch?: string | null;
  idempotencyKey?: string;
  signal?: AbortSignal;
  /** Public uçlar: CSRF başlığı ve oturum yönlendirmesi uygulanmaz. */
  anonymous?: boolean;
  /** Oturum yoklaması (/me): 401 beklenen bir sonuçtur, "oturum bitti" işleyicisi çağrılmaz. */
  probe?: boolean;
}

let csrfToken: string | null = null;
let onUnauthenticated: (() => void) | null = null;

export function setCsrfToken(token: string | null): void {
  csrfToken = token;
}

export function setUnauthenticatedHandler(handler: (() => void) | null): void {
  onUnauthenticated = handler;
}

async function fetchAnonymousCsrfToken(): Promise<string> {
  const result = await send<{ csrfToken: string }>("/api/auth/csrf", { method: "GET" }, null);
  return result.data.csrfToken;
}

async function refreshCsrfToken(): Promise<string> {
  // Oturum varsa kullanıcıya bağlı token /me'den, yoksa anonim token /csrf'ten gelir.
  try {
    const me = await send<{ csrfToken: string }>("/api/auth/me", { method: "GET" }, null);
    csrfToken = me.data.csrfToken;
  } catch (error) {
    if (error instanceof ApiError && error.status === 401) {
      csrfToken = await fetchAnonymousCsrfToken();
    } else {
      throw error;
    }
  }
  return csrfToken;
}

function parseRetryAfter(value: string | null): number | null {
  if (!value) return null;
  const seconds = Number(value);
  return Number.isFinite(seconds) && seconds >= 0 ? seconds : null;
}

async function send<T>(path: string, options: RequestOptions, token: string | null): Promise<ApiResult<T>> {
  const method = options.method ?? "GET";
  const headers: Record<string, string> = { Accept: "application/json" };
  let body: string | undefined;
  if (options.body !== undefined) {
    headers["Content-Type"] = "application/json";
    body = JSON.stringify(options.body);
  }
  if (token) headers["X-CSRF-TOKEN"] = token;
  if (options.ifMatch) headers["If-Match"] = options.ifMatch;
  if (options.idempotencyKey) headers["Idempotency-Key"] = options.idempotencyKey;

  let response: Response;
  try {
    response = await fetch(path, {
      method,
      headers,
      body,
      credentials: "same-origin",
      cache: "no-store",
      redirect: "error",
      referrerPolicy: "no-referrer",
      signal: options.signal,
    });
  } catch (error) {
    if (error instanceof DOMException && error.name === "AbortError") throw error;
    throw new NetworkError();
  }

  const etag = response.headers.get("ETag");
  if (!response.ok) {
    let problem: ProblemBody = { status: response.status };
    try {
      const parsed: unknown = await response.json();
      if (parsed && typeof parsed === "object") problem = parsed as ProblemBody;
    } catch {
      // Gövdesiz veya JSON olmayan hata yanıtı: yalnız durum kodu kullanılır.
    }
    throw new ApiError(response.status, problem, etag, parseRetryAfter(response.headers.get("Retry-After")));
  }

  let data: unknown = undefined;
  if (response.status !== 204) {
    try {
      data = await response.json();
    } catch {
      // Başarı durumu geldi ama gövde okunamadı: sonuç bilinmiyor sayılır (yanıt kaybı).
      throw new NetworkError();
    }
  }
  return {
    data: data as T,
    status: response.status,
    etag,
    location: response.headers.get("Location"),
    replayed: response.headers.get("Idempotency-Replayed") === "true",
  };
}

export async function request<T>(path: string, options: RequestOptions = {}): Promise<ApiResult<T>> {
  const method = options.method ?? "GET";
  const needsCsrf = method !== "GET" && !options.anonymous;
  try {
    if (needsCsrf && !csrfToken) csrfToken = await fetchAnonymousCsrfToken();
    try {
      return await send<T>(path, options, needsCsrf ? csrfToken : null);
    } catch (error) {
      // CSRF reddi işten ÖNCE gerçekleşir; token yenilenip bir kez daha denemek güvenlidir.
      if (needsCsrf && error instanceof ApiError && error.code === "csrf-validation-failed") {
        const fresh = await refreshCsrfToken();
        return await send<T>(path, options, fresh);
      }
      throw error;
    }
  } catch (error) {
    if (!options.anonymous && !options.probe && error instanceof ApiError && error.status === 401 && error.code === "unauthenticated") {
      onUnauthenticated?.();
    }
    throw error;
  }
}

export function toQuery(params: Record<string, string | number | boolean | readonly string[] | null | undefined>): string {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value === null || value === undefined || value === "" || value === false) continue;
    if (Array.isArray(value)) {
      for (const item of value as readonly string[]) search.append(key, item);
    } else {
      search.append(key, String(value));
    }
  }
  const text = search.toString();
  return text ? `?${text}` : "";
}
