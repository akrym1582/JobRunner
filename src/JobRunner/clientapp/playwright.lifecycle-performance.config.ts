import { defineConfig } from "@playwright/test";
import regular from "./playwright.config";
export default defineConfig({
  ...regular,
  testMatch: "**/lifecycle-performance.perf.ts",
  timeout: 600000,
  use: { ...regular.use, trace: "retain-on-failure" },
});
