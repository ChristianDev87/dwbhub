import { expect, test } from "@playwright/test";
import { ensureSetupCompleted, SETUP_DEFAULTS } from "./helpers/bootstrap";

const {
  tenantSlug: SLUG,
  ownerEmail: OWNER_EMAIL,
  ownerPassword: OWNER_PASSWORD,
} = SETUP_DEFAULTS;

test.describe("Plan 0.6 guilds flow", () => {
  test.beforeAll(async ({ request }) => {
    await ensureSetupCompleted(request);
  });

  test("login → guilds page → add → see in list → delete → empty", async ({
    page,
  }) => {
    await page.goto("/login");
    await page.fill('[data-testid="input-tenantSlug"]', SLUG);
    await page.fill('[data-testid="input-email"]', OWNER_EMAIL);
    await page.fill('[data-testid="input-password"]', OWNER_PASSWORD);
    await page.click('[data-testid="login-submit"]');
    await page.waitForURL(`**/t/${SLUG}/dashboard`, { timeout: 15_000 });

    await page.goto(`/t/${SLUG}/guilds`);
    await expect(page.getByTestId("input-discord-guild-id")).toBeVisible();

    // Use a unique Discord snowflake to avoid a 409 conflict when another
    // spec (e.g. bot-credentials) has registered a fixed ID and its cleanup
    // did not finish before this test runs.
    const discordId = String(
      BigInt(Date.now()) * 10000n + BigInt(Math.floor(Math.random() * 10000)),
    );
    await page.fill('[data-testid="input-discord-guild-id"]', discordId);
    await page.fill(
      '[data-testid="input-guild-display-name"]',
      "E2E Test Server",
    );
    await page.click('[data-testid="add-guild-submit"]');
    await expect(page.getByText("E2E Test Server")).toBeVisible();

    const row = page
      .locator("li")
      .filter({ hasText: "E2E Test Server" })
      .first();
    await row.locator('[data-testid="delete-guild-button"]').click();
    await expect(page.getByTestId("delete-confirm-yes")).toBeVisible();
    await page.click('[data-testid="delete-confirm-yes"]');
    await expect(
      page.getByText("E2E Test Server", { exact: true }),
    ).not.toBeVisible({
      timeout: 5_000,
    });
  });

  test("guarded /guilds without auth redirects to /login", async ({ page }) => {
    await page.goto(`/t/${SLUG}/guilds`);
    await page.waitForURL("**/login", { timeout: 15_000 });
  });
});
