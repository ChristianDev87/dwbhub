import { expect, test } from "@playwright/test";
import { getHealth, getSetupStatus } from "./helpers/api/global";

// Plan 0.3d's SetupGuard redirects `/` to `/setup` until the wizard completes.
// In e2e CI the DB is always fresh when the first browser project (chromium)
// starts, so `/` redirects to `/setup`. By the time the second project (firefox)
// runs, setup has been completed by auth.spec.ts. The two redirect tests are
// therefore adaptive: they assert the redirect to /setup when setup is pending,
// or pass immediately when setup is already done (nothing to verify).

test.describe("Plan 0.3d smoke (post-SetupGuard)", () => {
  test("fresh boot redirects to /setup and renders German heading", async ({
    page,
    request,
  }) => {
    if ((await getSetupStatus(request))?.completed) {
      // Setup already done on a prior spec's beforeAll; the redirect only fires
      // once. Nothing to assert here — health check in the third test covers
      // that the stack is up.
      return;
    }
    await page.goto("/");
    // SetupGuard fetches /api/setup/status then navigate("/setup", {replace:true}).
    await page.waitForURL("**/setup", { timeout: 15_000 });

    await expect(page.getByRole("heading", { level: 1 })).toHaveText(
      "Erstes Setup",
    );
  });

  test("english locale via ?lang=en flips the SetupPage heading", async ({
    page,
    request,
  }) => {
    if ((await getSetupStatus(request))?.completed) {
      // Same reasoning as above.
      return;
    }
    await page.goto("/?lang=en");
    await page.waitForURL("**/setup**", { timeout: 15_000 });

    await expect(page.getByRole("heading", { level: 1 })).toHaveText(
      "First-time setup",
    );
  });

  test("api /api/health responds 200 with version info", async ({
    request,
  }) => {
    const body = await getHealth(request);
    expect(body).toHaveProperty("status", "ok");
    expect(body).toHaveProperty("version");
  });
});
