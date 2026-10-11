import { defineConfig, devices } from "@playwright/test";

// E2E: gerçek tarayıcı, gerçek API, ayrı (izole) Compose yığını ve ayrı veritabanı. Başarı yolları mock'lanmaz.
// Yığın: scripts/local-stack.ps1 up -Project lst-e2e -LocalDir .local-e2e -Port 18443 -SeedDemo -Simulation
// Demo parolaları LST_E2E_SEED_DIR altındaki özel dosyalardan okunur; hiçbir çıktıya yazılmaz.
// Trace ve video KAPALI: oturum cookie'si, CSRF token'ı ve parola içeren ağ/klavye izleri artifact'a girmesin.
const baseURL = process.env.LST_E2E_BASE_URL ?? "https://localhost:18443";
const channel = process.env.LST_E2E_CHANNEL || undefined;

export default defineConfig({
  testDir: "./e2e",
  outputDir: "./test-results",
  fullyParallel: false,
  workers: 1,
  forbidOnly: !!process.env.CI,
  retries: 0,
  timeout: 60_000,
  expect: { timeout: 10_000 },
  reporter: [["list"], ["html", { outputFolder: "playwright-report", open: "never" }], ["junit", { outputFile: "test-results/junit.xml" }]],
  use: {
    baseURL,
    ignoreHTTPSErrors: true,
    locale: "tr-TR",
    timezoneId: "Europe/Istanbul",
    trace: "off",
    video: "off",
    screenshot: "only-on-failure",
  },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"], channel, viewport: { width: 1440, height: 900 } } }],
});
