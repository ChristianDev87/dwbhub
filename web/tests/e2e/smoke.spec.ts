import { expect, test } from "@playwright/test";

test.describe("Plan 0.1 smoke", () => {
  test("landing page renders German heading and probes the API", async ({ page }) => {
    await page.goto("/");

    await expect(page.getByRole("heading", { level: 1 })).toHaveText("Hallo DwbHub");

    const status = page.getByTestId("api-status");
    await expect(status).toBeVisible();

    // The card may briefly show the loading state; eventually it must reach OK.
    await expect(page.getByTestId("api-status-ok")).toBeVisible({ timeout: 15_000 });
    await expect(page.getByTestId("api-status-ok")).toContainText(/Version/i);
  });

  test("english locale via ?lang=en flips the heading", async ({ page }) => {
    await page.goto("/?lang=en");

    await expect(page.getByRole("heading", { level: 1 })).toHaveText("Hello DwbHub");
    await expect(page.getByTestId("api-status-ok")).toBeVisible({ timeout: 15_000 });
  });
});
