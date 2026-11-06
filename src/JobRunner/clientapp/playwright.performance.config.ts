import { defineConfig } from "@playwright/test";
import regular from "./playwright.config";

export default defineConfig({
  ...regular,
  testMatch: "**/editor-performance.perf.ts",
  timeout: 120000,
  use: { ...regular.use, trace: "retain-on-failure" },
});
