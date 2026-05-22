import { expect, test } from "@playwright/test";

// Plan 0.3d's SetupGuard redirects `/` to `/setup` until the wizard completes.
// In e2e CI the DB is always fresh, so `/` always lands on the setup wizard.
// These smoke tests verify the redirect + SetupPage heading, plus a direct
// /api/health probe (the HelloPage's status card is unreachable until setup
// is completed — that's covered by a post-setup smoke test in a later plan).

test.describe("Plan 0.3d smoke (post-SetupGuard)", () => {
  test("fresh boot redirects to /setup and renders German heading", async ({
    page,
  }) => {
    await page.goto("/");
    // SetupGuard fetches /api/setup/status then navigate("/setup", {replace:true}).
    await page.waitForURL("**/setup", { timeout: 15_000 });

    await expect(page.getByRole("heading", { level: 1 })).toHaveText(
      "Erstes Setup",
    );
  });

  test("english locale via ?lang=en flips the SetupPage heading", async ({
    page,
  }) => {
    await page.goto("/?lang=en");
    await page.waitForURL("**/setup**", { timeout: 15_000 });

    await expect(page.getByRole("heading", { level: 1 })).toHaveText(
      "First-time setup",
    );
  });

  test("api /api/health responds 200 with version info", async ({ request }) => {
    const res = await request.get("/api/health");
    expect(res.status()).toBe(200);
    const body = await res.json();
    expect(body).toHaveProperty("status", "ok");
    expect(body).toHaveProperty("version");
  });
});
