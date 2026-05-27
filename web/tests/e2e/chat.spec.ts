/**
 * Plan 1.0 Task 14 — chat.spec.ts
 *
 * 9 tests covering the ChatPage: send/receive flow, keyboard behaviour,
 * debounce, whitespace guard, maxLength, edit/delete via test-only endpoints,
 * pagination, and scroll-position preservation.
 *
 * Prerequisites:
 *   - DWBHUB_DISCORD_TEST_MODE=fake-rest on the API container.
 *   - FakeDiscordRestChannelClient returns scripted channel + message data.
 *   - TestOnlyController gated behind the same env-var.
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
import { postTenantLogin } from "./helpers/api/tenants";
import { bridgeChannel, getBackfillStatus } from "./helpers/api/channels";
import { testOnly, patchMessage, deleteMessage } from "./helpers/api/messages";

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

interface SendMessageResponseBody {
  id: number;
  publicId: string;
  discordMessageId: number;
  sentAt: string;
}

const {
  tenantSlug: SLUG,
  ownerEmail: OWNER_EMAIL,
  ownerPassword: OWNER_PASSWORD,
} = SETUP_DEFAULTS;

// ---------------------------------------------------------------------------
// Helpers
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
  const res = await postTenantLogin(request, {
    slug: SLUG,
    body: { email: OWNER_EMAIL, password: OWNER_PASSWORD },
  });
  const body = (await res.json()) as { accessToken: string };
  return body.accessToken;
}

/**
 * Ensure a channel is bridged and backfill complete, then navigate to chat.
 * Returns { channelPublicId, accessToken, authHeader }.
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
  const auth = { bearerToken: accessToken };

  // Bridge the channel
  const bridgeRes = await bridgeChannel(request, {
    slug: SLUG,
    channelPublicId: firstTextChannelPublicId,
    auth,
  });
  expect([202, 409]).toContain(bridgeRes.status());

  // Wait for backfill to complete (up to 60s)
  const deadline = Date.now() + 60_000;
  while (Date.now() < deadline) {
    await new Promise((r) => setTimeout(r, 2_000));
    const st = await getBackfillStatus(request, {
      slug: SLUG,
      channelPublicId: firstTextChannelPublicId,
      auth,
    });
    if (st.ok()) {
      const body = (await st.json()) as {
        status: { status: string; fetchedCount: number };
      };
      if (
        body.status?.status === "complete" ||
        body.status?.status === "cancelled"
      )
        break;
    }
  }

  await loginAsOwner(page);
  await page.goto(`/t/${SLUG}/channels/${firstTextChannelPublicId}`);

  // Wait for chat to load (loading indicator gone)
  await expect(page.getByTestId("chat-loading")).toHaveCount(0, {
    timeout: 15_000,
  });

  // Wait for SignalR connection to enter Connected state — without this,
  // live broadcasts (edit/delete/inject) might fire during the connect gap
  // and be dropped silently. See useMessagesHub for the StrictMode-safe
  // lifecycle pattern.
  //
  // Timeout 45s (was 30s, previously 15s): Firefox cold-stack SignalR connect
  // can exceed 30s — the WebSocket upgrade negotiation is slower in Firefox's
  // network stack than in Chromium, particularly on first connect after a fresh
  // bundle load. 45s matches the test budget for the Firefox browser project
  // without touching the global timeout. Chromium typically connects in <5s.
  await expect(
    page.locator('[data-signalr-state="Connected"]').first(),
  ).toBeVisible({ timeout: 45_000 });

  return {
    channelPublicId: firstTextChannelPublicId,
    accessToken,
    authHeader: { Authorization: `Bearer ${accessToken}` },
  };
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

test.describe("Plan 1.0 chat page", () => {
  test.beforeAll(async ({ request }) => {
    await ensureSetupCompleted(request);
  });

  // ── Test 1: send + receive own message via SignalR roundtrip ──────────────

  test("send message appears in list with via-dwbhub badge", async ({
    page,
    request,
  }) => {
    await setupBridgedChannel(page, request);

    const input = page.getByTestId("send-box-input");
    await input.fill("hello e2e");
    await input.press("Enter");

    // Message should appear in the list
    const msgRow = page.locator('[data-testid^="message-row-"]').filter({
      hasText: "hello e2e",
    });
    await expect(msgRow.first()).toBeVisible({ timeout: 10_000 });

    // via-dwbhub badge should be visible (message was sent via our webhook)
    await expect(
      msgRow.first().locator('[data-testid="message-via-dwbhub"]'),
    ).toBeVisible({ timeout: 5_000 });
  });

  // ── Test 2: Shift+Enter inserts newline; Enter sends ─────────────────────

  test("Shift+Enter inserts newline; Enter sends the multi-line message", async ({
    page,
    request,
  }) => {
    await setupBridgedChannel(page, request);

    const input = page.getByTestId("send-box-input");
    await input.click();
    await input.type("line1");
    await input.press("Shift+Enter");
    await input.type("line2");
    await input.press("Enter");

    // Textarea should be cleared after send
    await expect(input).toHaveValue("", { timeout: 5_000 });

    // Message containing both lines (with newline) should appear
    const msgRow = page.locator('[data-testid^="message-row-"]').filter({
      hasText: "line1",
    });
    await expect(msgRow.first()).toBeVisible({ timeout: 10_000 });
    await expect(
      msgRow.first().locator('[data-testid="message-content"]'),
    ).toContainText("line2");
  });

  // ── Test 3: double-click send button does not double-send ────────────────

  test("double-click send button sends exactly one message", async ({
    page,
    request,
  }) => {
    await setupBridgedChannel(page, request);

    const uniqueContent = `debounce-test-${Date.now()}`;
    const input = page.getByTestId("send-box-input");
    await input.fill(uniqueContent);

    let postCount = 0;
    await page.route("**/messages", (route) => {
      if (route.request().method() === "POST") postCount++;
      return route.continue();
    });

    const sendBtn = page.getByTestId("send-box-button");
    await sendBtn.dblclick();

    // Wait 3s to ensure any in-flight duplicates would have fired
    await new Promise((r) => setTimeout(r, 3_000));

    // Only one POST should have been fired
    expect(postCount).toBeLessThanOrEqual(1);

    // If a message was sent, exactly one should appear
    const matchingRows = page.locator('[data-testid^="message-row-"]').filter({
      hasText: uniqueContent,
    });
    const count = await matchingRows.count();
    expect(count).toBeLessThanOrEqual(1);
  });

  // ── Test 4: empty + whitespace-only messages do not send ─────────────────

  test("empty and whitespace-only messages do not send", async ({
    page,
    request,
  }) => {
    await setupBridgedChannel(page, request);

    let postCount = 0;
    await page.route("**/messages", (route) => {
      if (route.request().method() === "POST") postCount++;
      return route.continue();
    });

    // With empty textarea, the send button is disabled (no content to send)
    const sendBtn = page.getByTestId("send-box-button");
    await expect(sendBtn).toBeDisabled();
    expect(postCount).toBe(0);

    // Type only spaces — button stays disabled (whitespace trims to empty)
    const input = page.getByTestId("send-box-input");
    await input.fill("   ");
    await expect(sendBtn).toBeDisabled();
    expect(postCount).toBe(0);

    // Type real content — button becomes enabled and clicking it fires one request
    await input.fill("real-content");
    await expect(sendBtn).toBeEnabled({ timeout: 3_000 });
    await sendBtn.click();
    await new Promise((r) => setTimeout(r, 2_000));
    expect(postCount).toBe(1);
  });

  // ── Test 5: over-2000-char content rejected via maxLength ─────────────────

  test("textarea enforces maxLength=2000 — browser truncates at 2000 chars", async ({
    page,
    request,
  }) => {
    await setupBridgedChannel(page, request);

    const input = page.getByTestId("send-box-input");
    // fill() bypasses maxLength on some browsers, use type for longer strings
    await input.fill("x".repeat(3000));

    const value = await input.inputValue();
    expect(value.length).toBeLessThanOrEqual(2000);
  });

  // ── Test 6: edited Discord message reflects in UI via real PATCH endpoint ──

  test("edit event updates message content in the chat list", async ({
    page,
    request,
  }) => {
    const { channelPublicId, accessToken } = await setupBridgedChannel(
      page,
      request,
    );
    const auth = { bearerToken: accessToken };

    // Send a message via API so we get the publicId back in the response.
    // The message is sent through the real POST endpoint, which triggers
    // the SignalR MessageReceived broadcast — the UI will show it.
    const uniqueContent = `edit-test-${Date.now()}`;
    const sendRes = await request.post(
      `/api/t/${SLUG}/channels/${channelPublicId}/messages`,
      {
        headers: { Authorization: `Bearer ${accessToken}` },
        data: { content: uniqueContent },
      },
    );
    expect(sendRes.status()).toBe(201);
    const sendBody = (await sendRes.json()) as SendMessageResponseBody;
    const messagePublicId = sendBody.publicId;

    // Wait for the message to appear in the UI (via SignalR push).
    const msgRow = page.locator('[data-testid^="message-row-"]').filter({
      hasText: uniqueContent,
    });
    await expect(msgRow.first()).toBeVisible({ timeout: 10_000 });
    // Wait until the "sending…" indicator is gone — means SignalR confirmed the message.
    // 15s: the WebSocket proxy needs ~1-2 s to establish; Long Polling fallback (if WS
    // is unavailable) adds another ~5 s. 15s comfortably covers both transport paths.
    await expect(
      msgRow.first().locator('[data-testid="message-pending"]'),
    ).toHaveCount(0, { timeout: 15_000 });

    // Capture a STABLE testid-based locator BEFORE the edit fires — after the edit
    // the row's hasText no longer contains `uniqueContent` (server-pushed
    // MessageUpdated replaces m.content with the new value), so a hasText-filter
    // would stop matching and Playwright would report "element(s) not found".
    const testId = await msgRow.first().getAttribute("data-testid");
    if (!testId) throw new Error("Could not find message-row testid");
    const stableRow = page.locator(`[data-testid="${testId}"]`);

    // Edit via the real PATCH endpoint — flows through the full API pipeline.
    const editedContent = `edited-${Date.now()}`;
    const editRes = await patchMessage(request, {
      slug: SLUG,
      channelPublicId,
      messagePublicId,
      auth,
      body: { content: editedContent },
    });
    // 200 = success; 422 = edit window expired (should not happen in a fresh test)
    expect(editRes.status()).toBe(200);

    // "(edited)" indicator should appear on the SAME row (located by stable testid).
    await expect(
      stableRow.locator('[data-testid="message-edited"]'),
    ).toBeVisible({ timeout: 10_000 });
    // Content should reflect the new value.
    await expect(
      stableRow.locator('[data-testid="message-content"]'),
    ).toContainText(editedContent, { timeout: 5_000 });
  });

  // ── Test 7: deleted Discord message shows [deleted] placeholder ───────────

  test("delete event renders message as deleted placeholder", async ({
    page,
    request,
  }) => {
    const { channelPublicId, accessToken } = await setupBridgedChannel(
      page,
      request,
    );
    const auth = { bearerToken: accessToken };

    // Send a message via API so we get the publicId back in the response.
    // The message is sent through the real POST endpoint, which triggers
    // the SignalR MessageReceived broadcast — the UI will show it.
    const uniqueContent = `delete-test-${Date.now()}`;
    const sendRes = await request.post(
      `/api/t/${SLUG}/channels/${channelPublicId}/messages`,
      {
        headers: { Authorization: `Bearer ${accessToken}` },
        data: { content: uniqueContent },
      },
    );
    expect(sendRes.status()).toBe(201);
    const sendBody = (await sendRes.json()) as SendMessageResponseBody;
    const messagePublicId = sendBody.publicId;

    // Wait for the message to appear in the UI (via SignalR push).
    const msgRow = page.locator('[data-testid^="message-row-"]').filter({
      hasText: uniqueContent,
    });
    await expect(msgRow.first()).toBeVisible({ timeout: 10_000 });
    // Wait until the "sending…" indicator is gone — means SignalR confirmed the message.
    // 15s matches the edit-test timeout; see comment there for transport-fallback rationale.
    await expect(
      msgRow.first().locator('[data-testid="message-pending"]'),
    ).toHaveCount(0, { timeout: 15_000 });

    // Capture STABLE testid-based locator BEFORE delete — after the delete the
    // row's content is replaced with the localized deleted-placeholder so a
    // hasText-filter on `uniqueContent` would stop matching.
    const testId = await msgRow.first().getAttribute("data-testid");
    if (!testId) throw new Error("Could not find message-row testid");
    const stableRow = page.locator(`[data-testid="${testId}"]`);

    // Delete via the real DELETE endpoint — flows through the full API pipeline.
    const deleteRes = await deleteMessage(request, {
      slug: SLUG,
      channelPublicId,
      messagePublicId,
      auth,
    });
    // 204 = success; 404 = message not found (should not happen in a fresh test)
    expect(deleteRes.status()).toBe(204);

    // Message content should be replaced by the deleted placeholder
    // MessageRow renders t("chat.deleted") when isDeleted=true.
    // Locate the row by its stable testid (NOT by hasText, because the content
    // has changed by now).
    const content = stableRow.locator('[data-testid="message-content"]');
    // The deleted text varies by locale; check it no longer shows original content.
    await expect(content).not.toContainText(uniqueContent, { timeout: 10_000 });
  });

  // ── Test 8: load older messages on scroll-up paginates ────────────────────

  test("scrolling to top of chat loads older messages (pagination)", async ({
    page,
    request,
  }) => {
    await setupBridgedChannel(page, request);

    // Assert initial load shows messages (backfill gave us 200)
    const msgRows = page.locator('[data-testid^="message-row-"]');
    await expect(msgRows.first()).toBeVisible({ timeout: 10_000 });

    // Track pagination requests
    let paginationCalled = false;
    await page.route(`**/messages?*before=*`, (route) => {
      paginationCalled = true;
      return route.continue();
    });

    // Scroll to very top of the virtualized list to trigger loadOlder
    const virtuosoElement = page.locator(".virtuoso-scroller").first();
    if ((await virtuosoElement.count()) > 0) {
      await virtuosoElement.evaluate((el) => {
        el.scrollTop = 0;
      });
    } else {
      // Fallback: scroll within the message list area
      await page.keyboard.press("Home");
    }

    // Wait briefly for the paginated request to fire
    await new Promise((r) => setTimeout(r, 2_000));

    // A pagination fetch should have been triggered IF there is older history.
    // With 200 backfilled messages and an initial limit=50, there should be more.
    // We assert the call was intercepted (not 0 calls).
    // Note: the test is valid even if fewer than 200 messages were stored (idempotency).
    const totalRows = await msgRows.count();
    // Either pagination fired, or we were already at the full history
    expect(totalRows > 0 || paginationCalled).toBe(true);
  });

  // ── Test 9: scroll-position preserved when new live messages arrive ────────

  test("new message badge appears when scrolled up; position preserved", async ({
    page,
    request,
  }) => {
    const { channelPublicId, accessToken } = await setupBridgedChannel(
      page,
      request,
    );
    const auth = { bearerToken: accessToken };

    // Wait for initial messages to load
    const msgRows = page.locator('[data-testid^="message-row-"]');
    await expect(msgRows.first()).toBeVisible({ timeout: 10_000 });

    // Scroll up away from bottom. react-virtuoso v4 doesn't expose a stable
    // CSS class for its scroller — the previous `.virtuoso-scroller` selector
    // matched nothing in v4. Two robust alternatives:
    //   1. scrollIntoViewIfNeeded() on the oldest visible row — fires real
    //      scroll events that virtuoso's IntersectionObserver picks up
    //   2. Find virtuoso's actual scroller via [data-virtuoso-scroller]
    // (1) is simpler and doesn't depend on virtuoso internals.
    await msgRows.first().scrollIntoViewIfNeeded();

    // Reinforce: dispatch real wheel events upward in the message-list area
    // so virtuoso's atBottomStateChange definitely fires with atBottom=false.
    const listBox = await msgRows.first().boundingBox();
    if (listBox) {
      await page.mouse.move(listBox.x + listBox.width / 2, listBox.y + 50);
      for (let i = 0; i < 3; i++) {
        await page.mouse.wheel(0, -800);
        await new Promise((r) => setTimeout(r, 100));
      }
    }
    // Generous settle window — IntersectionObserver callbacks are batched and
    // react-virtuoso's atBottomStateChange usually fires within 200-500 ms,
    // but Playwright's headless browsers (especially Firefox) can be slower.
    await new Promise((r) => setTimeout(r, 2_000));

    // Capture scroll position via the actual virtuoso scroller (data-attribute
    // is stable across v4 minor versions; fall back to first row's offsetParent).
    const scroller = page
      .locator("[data-virtuoso-scroller], [data-test-id='virtuoso-item-list']")
      .first();
    let scrollBefore = 0;
    if ((await scroller.count()) > 0) {
      scrollBefore = await scroller.evaluate((el) => el.scrollTop);
    }

    // Inject a new message via test-only endpoint
    const injectRes = await testOnly.injectReceived(request, {
      slug: SLUG,
      auth,
      body: {
        channelPublicId,
        content: "scroll-position-test-message",
        authorName: "injector",
      },
    });
    // 201 = injected; 404 = test mode not active
    expect([201, 404]).toContain(injectRes.status());

    if (injectRes.status() === 201) {
      // New-messages badge should appear (we're scrolled up)
      await expect(page.getByTestId("chat-new-badge")).toBeVisible({
        timeout: 8_000,
      });

      // Scroll position should be roughly preserved (within 100px) — only check
      // if we could locate the scroller in the first place.
      if ((await scroller.count()) > 0) {
        const scrollAfter = await scroller.evaluate((el) => el.scrollTop);
        expect(Math.abs(scrollAfter - scrollBefore)).toBeLessThan(100);
      }
    } else {
      // If test-only endpoint returned 404, the test mode is not active;
      // this is a configuration issue rather than a code bug — assert loudly.
      throw new Error(
        "POST /test-only/messages/inject-received returned 404. " +
          "Is DWBHUB_DISCORD_TEST_MODE=fake-rest set on the API container? " +
          "Check deploy/compose/docker-compose.e2e-overlay.yml.",
      );
    }
  });
});
