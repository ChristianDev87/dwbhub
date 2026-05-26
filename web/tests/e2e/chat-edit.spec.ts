/**
 * Plan 1.1 Task 11 — chat-edit.spec.ts
 *
 * E2E test: user can edit their own outbound message via the 3-dot menu +
 * inline edit-mode textarea.
 *
 * Prerequisites:
 *   - DWBHUB_DISCORD_TEST_MODE=fake-rest on the API container.
 *   - FakeDiscordRestChannelClient returns scripted channel data.
 *   - Owner account is seeded via bootstrap helper.
 */

import {
  expect,
  test,
  type APIRequestContext,
  type Page,
} from "@playwright/test";
import {
  ensureSetupCompleted,
  seedActiveGuild,
  SETUP_DEFAULTS,
} from "./helpers/bootstrap";

const {
  tenantSlug: SLUG,
  ownerEmail: OWNER_EMAIL,
  ownerPassword: OWNER_PASSWORD,
} = SETUP_DEFAULTS;

// ---------------------------------------------------------------------------
// Helpers (mirrors chat.spec.ts pattern)
// ---------------------------------------------------------------------------

async function loginAsOwner(page: Page): Promise<void> {
  await page.goto("/login");
  await page.fill('[data-testid="input-tenantSlug"]', SLUG);
  await page.fill('[data-testid="input-email"]', OWNER_EMAIL);
  await page.fill('[data-testid="input-password"]', OWNER_PASSWORD);
  await page.click('[data-testid="login-submit"]');
  await page.waitForURL(`**/t/${SLUG}/dashboard`, { timeout: 15_000 });
}

async function apiLogin(request: APIRequestContext): Promise<string> {
  const res = await request.post(`/api/tenants/${SLUG}/auth/login`, {
    data: { email: OWNER_EMAIL, password: OWNER_PASSWORD },
  });
  const body = (await res.json()) as { accessToken: string };
  return body.accessToken;
}

/**
 * Seed a bridged channel, wait for backfill, navigate to chat and wait for
 * SignalR Connected state. Returns accessToken + channelPublicId.
 */
async function setupBridgedChannel(
  page: Page,
  request: APIRequestContext,
): Promise<{
  channelPublicId: string;
  accessToken: string;
  authHeader: { Authorization: string };
}> {
  const { firstTextChannelPublicId } = await seedActiveGuild(request);
  if (!firstTextChannelPublicId) {
    throw new Error("No text channel from seedActiveGuild");
  }

  const accessToken = await apiLogin(request);
  const authHeader = { Authorization: `Bearer ${accessToken}` };

  // Bridge the channel
  const bridgeRes = await request.post(
    `/api/t/${SLUG}/channels/${firstTextChannelPublicId}/bridge`,
    { headers: authHeader },
  );
  expect([202, 409]).toContain(bridgeRes.status());

  // Wait for backfill to complete (up to 60s)
  const deadline = Date.now() + 60_000;
  while (Date.now() < deadline) {
    await new Promise((r) => setTimeout(r, 2_000));
    const st = await request.get(
      `/api/t/${SLUG}/channels/${firstTextChannelPublicId}/backfill-status`,
      { headers: authHeader },
    );
    if (st.ok()) {
      const body = (await st.json()) as { status: { status: string } };
      if (
        body.status?.status === "complete" ||
        body.status?.status === "cancelled"
      )
        break;
    }
  }

  await loginAsOwner(page);
  await page.goto(`/t/${SLUG}/channels/${firstTextChannelPublicId}`);

  await expect(page.getByTestId("chat-loading")).toHaveCount(0, {
    timeout: 15_000,
  });
  await expect(
    page.locator('[data-signalr-state="Connected"]').first(),
  ).toBeVisible({
    timeout: 30_000,
  });

  return { channelPublicId: firstTextChannelPublicId, accessToken, authHeader };
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

test.describe("Plan 1.1 chat edit", () => {
  test.beforeAll(async ({ request }) => {
    await ensureSetupCompleted(request);
  });

  test("user can edit own outbound message", async ({ page, request }) => {
    await setupBridgedChannel(page, request);

    // Send a message
    const sendInput = page.getByTestId("send-box-input");
    const uniqueContent = `edit-me-${Date.now()}`;
    await sendInput.fill(uniqueContent);
    await sendInput.press("Enter");

    // Wait for message to appear and the pending state to clear
    const msgRow = page
      .locator('[data-testid^="message-row-"]')
      .filter({ hasText: uniqueContent });
    await expect(msgRow.first()).toBeVisible({ timeout: 10_000 });
    await expect(
      msgRow.first().locator('[data-testid="message-pending"]'),
    ).toHaveCount(0, { timeout: 15_000 });

    // Hover to reveal the 3-dot menu trigger
    await msgRow.first().hover();

    // Click the trigger button
    const trigger = msgRow
      .first()
      .locator('[data-testid="message-actions-trigger"]');
    await expect(trigger).toBeVisible({ timeout: 5_000 });
    await trigger.click();

    // Click Edit
    const editBtn = page.getByTestId("message-actions-edit");
    await expect(editBtn).toBeVisible({ timeout: 5_000 });
    await editBtn.click();

    // Inline edit textarea should appear, auto-focused
    const textarea = page.getByTestId("message-edit-textarea");
    await expect(textarea).toBeVisible({ timeout: 5_000 });

    // Type the new content (clear first, then fill)
    await textarea.fill("hello world edited");
    await textarea.press("Enter");

    // Capture stable row locator by testid
    const testId = await msgRow.first().getAttribute("data-testid");
    const stableRow = testId
      ? page.locator(`[data-testid="${testId}"]`)
      : msgRow.first();

    // Edited content should appear
    await expect(
      stableRow.locator('[data-testid="message-content"]'),
    ).toContainText("hello world edited", { timeout: 10_000 });

    // "(edited)" indicator should appear
    await expect(
      stableRow.locator('[data-testid="message-edited"]'),
    ).toBeVisible({ timeout: 10_000 });
  });
});
