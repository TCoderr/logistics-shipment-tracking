import { readFileSync } from "node:fs";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vitest/config";

// Geliştirme: SPA Vite'tan, /api + /health + /realtime aynı origin üzerinden ASP.NET host'una (HTTPS) proxy'lenir.
// Cookie'ler __Host- önekli ve Secure'dur: Chromium/Firefox http://localhost'u güvenli bağlam sayar. Başka tarayıcı için
// LST_WEB_HTTPS_PFX (+ LST_WEB_HTTPS_PFX_PASSWORD_FILE) verilirse dev sunucusu HTTPS açar. Parola dosyadan okunur, loglanmaz.
const backend = process.env.LST_API_ORIGIN ?? "https://localhost:7210";
const pfxPath = process.env.LST_WEB_HTTPS_PFX;
const pfxPasswordFile = process.env.LST_WEB_HTTPS_PFX_PASSWORD_FILE;

const proxyTarget = { target: backend, changeOrigin: false, secure: false } as const;

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    strictPort: true,
    https: pfxPath
      ? { pfx: readFileSync(pfxPath), passphrase: pfxPasswordFile ? readFileSync(pfxPasswordFile, "utf8") : undefined }
      : undefined,
    proxy: {
      "/api": proxyTarget,
      "/health": proxyTarget,
      "/realtime": { ...proxyTarget, ws: true },
    },
  },
  build: {
    outDir: "dist",
    sourcemap: false,
    target: "es2022",
  },
  test: {
    environment: "jsdom",
    globals: true,
    setupFiles: ["./src/test/setup.ts"],
    include: ["src/**/*.test.{ts,tsx}"],
    restoreMocks: true,
  },
});
