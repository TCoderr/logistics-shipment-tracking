import { readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { expect, request as playwrightRequest, type APIRequestContext, type Page } from "@playwright/test";

const here = path.dirname(fileURLToPath(import.meta.url));
const seedDir = process.env.LST_E2E_SEED_DIR ?? path.resolve(here, "../../../.local-e2e/seed");
const baseURL = process.env.LST_E2E_BASE_URL ?? "https://localhost:18443";

export type DemoUser = "ops.agent" | "ops.manager" | "hub.ist" | "hub.ank" | "hub.izm";

/** Demo kullanıcı parolası özel dosyadan okunur; değer asla loglanmaz veya assertion mesajına girmez. */
export function passwordOf(user: DemoUser): string {
  return readFileSync(path.join(seedDir, `Seed__Passwords__${user}`), "utf8");
}

/** Bu koşuya özel, açıkça sentetik etiket (ör. sipariş referansı): veriler koşular arasında karışmaz. */
export function runTag(prefix: string): string {
  return `E2E-${prefix}-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`.toUpperCase();
}

export const synthetic = {
  senderName: "Sentetik Gonderen E2E",
  senderPhone: "+902125550101",
  senderLine1: "Sentetik Cd. No: 11",
  recipientName: "Sentetik Alici E2E",
  recipientPhone: "+905325550199",
  recipientLine1: "Ornek Mh. Deneme Sk. No: 7",
  recipientDistrict: "Cankaya",
  recipientPostalCode: "06420",
};

export async function loginViaUi(page: Page, user: DemoUser): Promise<void> {
  await page.goto("/login");
  await page.getByLabel("Kullanıcı adı").fill(user);
  await page.getByLabel("Parola").fill(passwordOf(user));
  await page.getByRole("button", { name: "Giriş yap" }).click();
  await expect(page.getByRole("button", { name: "Çıkış" })).toBeVisible();
}

export async function logoutViaUi(page: Page): Promise<void> {
  await page.getByRole("button", { name: "Çıkış" }).click();
  await expect(page.getByRole("button", { name: "Giriş yap" })).toBeVisible();
}

/** Gerçek API'ye kendi cookie kavanozuyla bağlanan istemci (arayüz dışı hazırlık adımları ve API reddi kontrolleri için). */
export class ApiSession {
  private constructor(
    readonly context: APIRequestContext,
    private csrf: string,
    readonly userId: string,
  ) {}

  static async login(user: DemoUser): Promise<ApiSession> {
    const context = await playwrightRequest.newContext({ baseURL, ignoreHTTPSErrors: true });
    const anonymous = (await (await context.get("/api/auth/csrf")).json()) as { csrfToken: string };
    const login = await context.post("/api/auth/login", { headers: { "X-CSRF-TOKEN": anonymous.csrfToken }, data: { userName: user, password: passwordOf(user) } });
    if (login.status() !== 204) throw new Error(`API girişi başarısız: ${user} → HTTP ${login.status()}`);
    const me = (await (await context.get("/api/auth/me")).json()) as { id: string; csrfToken: string };
    return new ApiSession(context, me.csrfToken, me.id);
  }

  async dispose(): Promise<void> {
    await this.context.dispose();
  }

  get(url: string) {
    return this.context.get(url);
  }

  post(url: string, options: { data?: unknown; ifMatch?: string; idempotencyKey?: string; omitCsrf?: boolean } = {}) {
    const headers: Record<string, string> = {};
    if (!options.omitCsrf) headers["X-CSRF-TOKEN"] = this.csrf;
    if (options.ifMatch) headers["If-Match"] = options.ifMatch;
    if (options.idempotencyKey) headers["Idempotency-Key"] = options.idempotencyKey;
    return this.context.post(url, { headers, data: options.data });
  }

  async createShipment(options: { externalReference: string; origin?: string; destination?: string; packages?: number; serviceLevel?: "Standard" | "Express" }) {
    const packages = Array.from({ length: options.packages ?? 1 }, () => ({ weightGrams: 1200, lengthMm: 300, widthMm: 200, heightMm: 100, contentDescription: "Sentetik E2E" }));
    const response = await this.post("/api/shipments", {
      idempotencyKey: `e2e-${crypto.randomUUID()}`,
      data: {
        serviceLevel: options.serviceLevel ?? "Standard",
        externalReference: options.externalReference,
        originHubCode: options.origin ?? "IST-01",
        destinationHubCode: options.destination ?? "ANK-02",
        sender: {
          fullName: synthetic.senderName,
          companyName: null,
          phone: synthetic.senderPhone,
          email: null,
          address: { line1: synthetic.senderLine1, line2: null, district: "Bagcilar", city: "Istanbul", postalCode: "34200", countryCode: "TR" },
        },
        recipient: {
          fullName: synthetic.recipientName,
          companyName: null,
          phone: synthetic.recipientPhone,
          email: null,
          address: {
            line1: synthetic.recipientLine1,
            line2: null,
            district: synthetic.recipientDistrict,
            city: "Ankara",
            postalCode: synthetic.recipientPostalCode,
            countryCode: "TR",
          },
        },
        packages,
        declaredValue: null,
      },
    });
    expect(response.status(), "gönderi oluşturma").toBe(201);
    return (await response.json()) as { id: string; trackingNumber: string; publicTrackingCode: string; packages: { labelCode: string }[] };
  }

  async scan(hubCode: string, labelCode: string, scanType: "Arrival" | "Departure" | "OutForDelivery") {
    const response = await this.post(`/api/hubs/${hubCode}/scans`, {
      data: { clientScanId: `e2e-${crypto.randomUUID()}`, labelCode, scanType, occurredAt: new Date().toISOString() },
    });
    expect(response.status(), `${hubCode} ${scanType} ${labelCode}`).toBe(200);
    return (await response.json()) as { outcome: string };
  }

  async detail(shipmentId: string) {
    const response = await this.get(`/api/shipments/${shipmentId}`);
    return { status: response.status(), etag: response.headers()["etag"] ?? "", body: (await response.json()) as Record<string, unknown> };
  }
}

/** IST-01 → ANK-02 gönderisini gerçek hub taramalarıyla "Dağıtımda" durumuna getirir. */
export async function driveToOutForDelivery(labels: string[]): Promise<void> {
  const ist = await ApiSession.login("hub.ist");
  const ank = await ApiSession.login("hub.ank");
  try {
    for (const label of labels) await ist.scan("IST-01", label, "Arrival");
    for (const label of labels) await ist.scan("IST-01", label, "Departure");
    for (const label of labels) await ank.scan("ANK-02", label, "Arrival");
    for (const label of labels) await ank.scan("ANK-02", label, "OutForDelivery");
  } finally {
    await ist.dispose();
    await ank.dispose();
  }
}

/** Sayfanın console'una yazılan her şeyi toplar (gizli değer sızıntısı kontrolü için). */
export function collectConsole(page: Page): string[] {
  const lines: string[] = [];
  page.on("console", (message) => lines.push(message.text()));
  page.on("pageerror", (error) => lines.push(String(error)));
  return lines;
}
