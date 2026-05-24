import { expect, test } from "@playwright/test";
import { ensureSetupCompleted, SETUP_DEFAULTS } from "./helpers/bootstrap";

const {
  tenantSlug: SLUG,
  ownerEmail: OWNER_EMAIL,
  ownerPassword: OWNER_PASSWORD,
} = SETUP_DEFAULTS;
const TEST_DISCORD_GUILD_ID = "1234567890123456789";
const TEST_BOT_TOKEN =
  "TestTokenSegment0000000000000000000000.NotReal.TestTokenFinalSegment000000000000000";

test.describe("Plan 0.7 bot-credentials flow", () => {
  test.beforeAll(async ({ request }) => {
    await ensureSetupCompleted(request);
  });

  test("login → guilds → configure bot-token → rotate → remove", async ({
    page,
  }) => {
    await page.goto("/login");
    await page.fill('[data-testid="input-tenantSlug"]', SLUG);
    await page.fill('[data-testid="input-email"]', OWNER_EMAIL);
    await page.fill('[data-testid="input-password"]', OWNER_PASSWORD);
    await page.click('[data-testid="login-submit"]');
    await page.waitForURL(`**/t/${SLUG}/dashboard`, { timeout: 15_000 });

    await page.goto(`/t/${SLUG}/guilds`);
    await page.fill(
      '[data-testid="input-discord-guild-id"]',
      TEST_DISCORD_GUILD_ID,
    );
    await page.fill(
      '[data-testid="input-guild-display-name"]',
      "Bot-Credentials E2E",
    );
    await page.click('[data-testid="add-guild-submit"]');
    await expect(page.getByText("Bot-Credentials E2E")).toBeVisible();

    await expect(
      page.getByTestId("bot-credentials-missing").first(),
    ).toBeVisible();

    await page.click('[data-testid="configure-bot-token-button"]');
    await expect(page.getByTestId("input-bot-token")).toBeVisible();
    await page.fill('[data-testid="input-bot-token"]', TEST_BOT_TOKEN);
    await page.click('[data-testid="bot-token-save"]');

    await expect(
      page.getByTestId("bot-credentials-configured").first(),
    ).toBeVisible();

    await page.click('[data-testid="configure-bot-token-button"]');
    await page.fill('[data-testid="input-bot-token"]', TEST_BOT_TOKEN);
    await page.click('[data-testid="bot-token-save"]');
    await expect(
      page.getByTestId("bot-credentials-configured").first(),
    ).toBeVisible();

    await page.click('[data-testid="remove-bot-token-button"]');
    await expect(page.getByTestId("bot-remove-confirm-yes")).toBeVisible();
    await page.click('[data-testid="bot-remove-confirm-yes"]');
    await expect(
      page.getByTestId("bot-credentials-missing").first(),
    ).toBeVisible();

    await page.click('[data-testid="delete-guild-button"]');
    await page.click('[data-testid="delete-confirm-yes"]');
  });

  test("guarded /guilds without auth redirects to /login", async ({ page }) => {
    await page.goto(`/t/${SLUG}/guilds`);
    await page.waitForURL("**/login", { timeout: 15_000 });
  });
});
