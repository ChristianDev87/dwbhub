import { defineConfig, devices } from "@playwright/test";

export default defineConfig({
  testDir: "./tests/e2e",
  // Only run TypeScript specs. Stray .js files from local tsc runs are gitignored
  // but still present in the working tree; without this match Playwright would
  // double-run every spec.
  testMatch: ["**/*.spec.ts"],
  fullyParallel: false,
  forbidOnly: !!process.env["CI"],
  retries: process.env["CI"] ? 2 : 0,
  workers: process.env["CI"] ? 1 : undefined,
  reporter: process.env["CI"]
    ? [["github"], ["html", { open: "never" }]]
    : "list",
  timeout: 30_000,
  use: {
    baseURL: process.env["E2E_BASE_URL"] ?? "http://localhost:5173",
    // The default smoke test asserts the German heading. i18next falls back to
    // navigator.language when no ?lang= or localStorage entry is set; without
    // pinning the locale here, Playwright's default en-US would cause the test
    // to load English copy.
    locale: "de-DE",
    trace: "on-first-retry",
    screenshot: "only-on-failure",
    video: "retain-on-failure",
  },
  projects: [
    { name: "chromium", use: { ...devices["Desktop Chrome"] } },
    { name: "firefox", use: { ...devices["Desktop Firefox"] } },
  ],
});
