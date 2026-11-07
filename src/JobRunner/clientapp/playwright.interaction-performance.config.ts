import { defineConfig } from "@playwright/test";
import regular from "./playwright.config";
export default defineConfig({
  ...regular,
  testMatch: "**/editor-interaction.perf.ts",
  timeout: 600000,
  expect: { timeout: 120000 },
});
