import { expect, test } from "@playwright/test";
import { ensureSetupCompleted, SETUP_DEFAULTS } from "./helpers/bootstrap";

const {
  tenantSlug: SLUG,
  ownerEmail: OWNER_EMAIL,
  ownerPassword: OWNER_PASSWORD,
} = SETUP_DEFAULTS;

test.describe("Plan 0.5 auth flow", () => {
  test.beforeAll(async ({ request }) => {
    await ensureSetupCompleted(request);
  });

  test("login → dashboard → logout", async ({ page }) => {
    await page.goto("/login");
    await expect(page.getByTestId("input-tenantSlug")).toBeVisible();

    await page.fill('[data-testid="input-tenantSlug"]', SLUG);
    await page.fill('[data-testid="input-email"]', OWNER_EMAIL);
    await page.fill('[data-testid="input-password"]', OWNER_PASSWORD);
    await page.click('[data-testid="login-submit"]');

    await page.waitForURL(`**/t/${SLUG}/dashboard`, { timeout: 15_000 });
    await expect(page.getByRole("heading", { level: 1 })).toContainText(
      /Welcome|Willkommen/,
    );

    await page.click('[data-testid="dashboard-logout"]');
    await page.waitForURL("**/login", { timeout: 15_000 });
  });

  test("guarded route without auth redirects to /login", async ({ page }) => {
    await page.goto(`/t/${SLUG}/dashboard`);
    await page.waitForURL("**/login", { timeout: 15_000 });
  });
});
