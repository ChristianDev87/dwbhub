import { expect, test, type Page } from "@playwright/test";
import { ensureSetupCompleted, SETUP_DEFAULTS } from "./helpers/bootstrap";

const {
  tenantSlug: SLUG,
  ownerEmail: OWNER_EMAIL,
  ownerPassword: OWNER_PASSWORD,
} = SETUP_DEFAULTS;

async function loginAsOwner(page: Page) {
  await page.goto("/login");
  await page.fill('[data-testid="input-tenantSlug"]', SLUG);
  await page.fill('[data-testid="input-email"]', OWNER_EMAIL);
  await page.fill('[data-testid="input-password"]', OWNER_PASSWORD);
  await page.click('[data-testid="login-submit"]');
  await page.waitForURL(`**/t/${SLUG}/dashboard`, { timeout: 15_000 });
}

test.describe("Plan 0.8 bot-connection UI", () => {
  test.beforeAll(async ({ request }) => {
    await ensureSetupCompleted(request);
  });

  let createdGuildPublicId: string | null = null;

  test.afterEach(async ({ page }) => {
    if (createdGuildPublicId === null) return;
    const publicIdToDelete = createdGuildPublicId;
    createdGuildPublicId = null;
    // Best-effort cleanup via UI delete button. If the UI flow is broken, this will
    // silently fail — preventing afterEach itself from crashing.
    try {
      await page.goto(`/t/${SLUG}/guilds`).catch(() => {});
      const deleteBtn = page
        .locator("li")
        .filter({ has: page.getByTestId(`guild-status-${publicIdToDelete}`) })
        .locator('[data-testid="delete-guild-button"]');
      if ((await deleteBtn.count()) > 0) {
        await deleteBtn.click({ timeout: 5_000 });
        await page.click('[data-testid="delete-confirm-yes"]', {
          timeout: 5_000,
        });
      }
    } catch {
      // crash-safe: never let cleanup fail the test
    }
  });

  test("activate then deactivate cycle updates buttons", async ({ page }) => {
    await loginAsOwner(page);
    await page.goto(`/t/${SLUG}/guilds`);

    // Use a unique Discord guild ID + display name to avoid collision across runs.
    // Discord snowflake IDs are 17-20 decimal digits. Date.now() is 13 digits in
    // 2026; multiplying by 10000 gives a 17-digit base that fits the validator.
    const uniqueDiscordId = String(
      BigInt(Date.now()) * 10000n + BigInt(Math.floor(Math.random() * 10000)),
    );
    const name = `Bot-Connection E2E ${uniqueDiscordId.slice(-6)}`;

    await page.fill('[data-testid="input-discord-guild-id"]', uniqueDiscordId);
    await page.fill('[data-testid="input-guild-display-name"]', name);
    await page.click('[data-testid="add-guild-submit"]');
    await expect(page.getByText(name)).toBeVisible();

    // Locate the li row containing this guild name.
    const row = page.locator("li").filter({ hasText: name }).first();

    // Extract publicId from the per-guild status testid.
    const statusEl = row.locator('[data-testid^="guild-status-"]').first();
    const statusTestId = await statusEl.getAttribute("data-testid");
    expect(statusTestId).toBeTruthy();
    const publicId = statusTestId!.replace("guild-status-", "");

    // Track for afterEach safety net.
    createdGuildPublicId = publicId;

    const pauseBtn = page.getByTestId(`guild-pause-${publicId}`);
    const resumeBtn = page.getByTestId(`guild-resume-${publicId}`);

    // New guild starts active → Pause visible, Resume absent.
    await expect(pauseBtn).toBeVisible();
    await expect(resumeBtn).toHaveCount(0);

    // Pause → modal → confirm → flips to inactive.
    await pauseBtn.click();
    await expect(page.getByTestId("pause-guild-modal")).toBeVisible();
    await page.click('[data-testid="pause-modal-confirm"]');
    await expect(page.getByTestId("pause-guild-modal")).toHaveCount(0);
    await expect(resumeBtn).toBeVisible({ timeout: 5_000 });
    await expect(pauseBtn).toHaveCount(0);

    // Resume → flips back to active.
    await resumeBtn.click();
    await expect(pauseBtn).toBeVisible({ timeout: 5_000 });
    await expect(resumeBtn).toHaveCount(0);

    // Status indicator remains gray (no credentials, no connection attempted).
    // Without credentials, manager.GetState returns null → UI shows "unknown".
    await expect(page.getByTestId(`guild-status-${publicId}`)).toContainText(
      /unknown|disconnected/i,
    );
  });

  test("reconnect button disabled without bot credentials", async ({
    page,
  }) => {
    await loginAsOwner(page);
    await page.goto(`/t/${SLUG}/guilds`);

    const uniqueDiscordId = String(
      BigInt(Date.now()) * 10000n + BigInt(Math.floor(Math.random() * 10000)),
    );
    const name = `Bot-Connection-Reconnect E2E ${uniqueDiscordId.slice(-6)}`;

    await page.fill('[data-testid="input-discord-guild-id"]', uniqueDiscordId);
    await page.fill('[data-testid="input-guild-display-name"]', name);
    await page.click('[data-testid="add-guild-submit"]');
    await expect(page.getByText(name)).toBeVisible();

    // Locate the li row containing this guild name.
    const row = page.locator("li").filter({ hasText: name }).first();

    // Extract publicId from the per-guild status testid.
    const statusEl = row.locator('[data-testid^="guild-status-"]').first();
    const publicId = (await statusEl.getAttribute("data-testid"))!.replace(
      "guild-status-",
      "",
    );

    // Track for afterEach safety net.
    createdGuildPublicId = publicId;

    const reconnectBtn = page.getByTestId(`guild-reconnect-${publicId}`);
    await expect(reconnectBtn).toBeVisible();
    // No bot credentials configured → reconnect is disabled.
    await expect(reconnectBtn).toBeDisabled();
  });
});
