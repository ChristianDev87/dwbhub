import { expect, test, type APIRequestContext } from "@playwright/test";

const SLUG = "acme";
const OWNER_EMAIL = "owner@acme.test";
const OWNER_PASSWORD = "correct horse battery staple";

async function setupReady(request: APIRequestContext): Promise<boolean> {
  const statusRes = await request.get("/api/setup/status");
  const status = (await statusRes.json()) as { completed: boolean };
  return status.completed;
}

test.describe("Plan 0.6 guilds flow", () => {
  test("login → guilds page → add → see in list → delete → empty", async ({
    page,
    request,
  }) => {
    const ok = await setupReady(request);
    test.skip(
      !ok,
      "Setup not pre-completed (skip — same convention as auth.spec.ts)",
    );

    await page.goto("/login");
    await page.fill('[data-testid="input-tenantSlug"]', SLUG);
    await page.fill('[data-testid="input-email"]', OWNER_EMAIL);
    await page.fill('[data-testid="input-password"]', OWNER_PASSWORD);
    await page.click('[data-testid="login-submit"]');
    await page.waitForURL(`**/t/${SLUG}/dashboard`, { timeout: 15_000 });

    await page.goto(`/t/${SLUG}/guilds`);
    await expect(page.getByTestId("input-discord-guild-id")).toBeVisible();

    const discordId = "1234567890123456789";
    await page.fill('[data-testid="input-discord-guild-id"]', discordId);
    await page.fill(
      '[data-testid="input-guild-display-name"]',
      "E2E Test Server",
    );
    await page.click('[data-testid="add-guild-submit"]');
    await expect(page.getByText("E2E Test Server")).toBeVisible();

    await page.click('[data-testid="delete-guild-button"]');
    await expect(page.getByTestId("delete-confirm-yes")).toBeVisible();
    await page.click('[data-testid="delete-confirm-yes"]');
    await expect(page.getByText("E2E Test Server")).not.toBeVisible({
      timeout: 5_000,
    });
  });

  test("guarded /guilds without auth redirects to /login", async ({
    page,
    request,
  }) => {
    const ok = await setupReady(request);
    test.skip(!ok, "Setup not pre-completed");

    await page.goto(`/t/${SLUG}/guilds`);
    await page.waitForURL("**/login", { timeout: 15_000 });
  });
});
