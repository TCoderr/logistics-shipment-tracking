import js from "@eslint/js";
import reactHooks from "eslint-plugin-react-hooks";
import globals from "globals";
import tseslint from "typescript-eslint";

export default tseslint.config(
  { ignores: ["dist", "node_modules", "playwright-report", "test-results", "e2e/.auth"] },
  {
    files: ["**/*.{ts,tsx}"],
    extends: [js.configs.recommended, ...tseslint.configs.recommendedTypeChecked, reactHooks.configs.flat.recommended],
    languageOptions: {
      ecmaVersion: 2023,
      globals: { ...globals.browser },
      parserOptions: { projectService: true, tsconfigRootDir: import.meta.dirname },
    },
    rules: {
      // Kimlik bilgisi, CSRF token'ı ve public takip kodu tarayıcı console'una yazılmamalı: console tamamen yasak.
      "no-console": "error",
      "@typescript-eslint/consistent-type-imports": "error",
      "@typescript-eslint/no-unused-vars": ["error", { argsIgnorePattern: "^_" }],
    },
  },
  {
    files: ["e2e/**/*.ts", "vite.config.ts", "playwright.config.ts"],
    languageOptions: { globals: { ...globals.node } },
  },
  { files: ["eslint.config.js"], languageOptions: { globals: { ...globals.node } } },
);
