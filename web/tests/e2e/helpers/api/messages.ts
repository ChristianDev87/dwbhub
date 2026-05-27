/**
 * Typed API helpers for message endpoints.
 *
 * Covers /api/t/{slug}/channels/{channelPublicId}/messages (GET + POST)
 * and the real edit/delete endpoints:
 *   PATCH  /api/t/{slug}/channels/{channelPublicId}/messages/{messagePublicId}
 *   DELETE /api/t/{slug}/channels/{channelPublicId}/messages/{messagePublicId}
 * and the remaining test-only namespace:
 *   /api/t/{slug}/test-only/messages/inject-received
 *
 * NOTE: testOnly.editMessage and testOnly.deleteMessage have been removed.
 * Use patchMessage / deleteMessage (real endpoints) instead — they work in
 * fake-rest mode via FakeDiscordRestChannelClient.
 *
 * Symmetric to DwbHub.Tests.Shared.Api.MessagesApi.
 */

import type { APIRequestContext } from "@playwright/test";
import type { paths } from "../../../../src/lib/api/generated/schema";

// ---------------------------------------------------------------------------
// Types derived from paths
// ---------------------------------------------------------------------------

type TestInjectReceivedRequest = NonNullable<
  paths["/api/t/{slug}/test-only/messages/inject-received"]["post"]["requestBody"]
>["content"]["application/json"];

type EditMessageRequest = NonNullable<
  paths["/api/t/{slug}/channels/{channelPublicId}/messages/{messagePublicId}"]["patch"]["requestBody"]
>["content"]["application/json"];

// ---------------------------------------------------------------------------
// /api/t/{slug}/channels/{channelPublicId}/messages — GET
// ---------------------------------------------------------------------------

/**
 * GET /api/t/{slug}/channels/{channelPublicId}/messages
 *
 * Returns raw response. Throws on non-2xx.
 */
export async function getMessages(
  request: APIRequestContext,
  args: {
    slug: string;
    channelPublicId: string;
    auth: { bearerToken: string };
    query?: { limit?: number; before?: number };
  },
) {
  const params: Record<string, string> = {};
  if (args.query?.limit !== undefined)
    params["limit"] = String(args.query.limit);
  if (args.query?.before !== undefined)
    params["before"] = String(args.query.before);

  return request.get(
    `/api/t/${args.slug}/channels/${args.channelPublicId}/messages`,
    {
      headers: { Authorization: `Bearer ${args.auth.bearerToken}` },
      params,
    },
  );
}

// ---------------------------------------------------------------------------
// /api/t/{slug}/channels/{channelPublicId}/messages/{messagePublicId} — PATCH
// ---------------------------------------------------------------------------

/**
 * PATCH /api/t/{slug}/channels/{channelPublicId}/messages/{messagePublicId}
 *
 * Edits an existing message through the real edit endpoint.
 * Returns raw response. 200 = success, 403 = forbidden, 404 = not found,
 * 422 = edit window expired, 404 = test mode not active (non-fake-rest env).
 *
 * The caller (Owner, who is also the message author) must provide their access token.
 */
export async function patchMessage(
  request: APIRequestContext,
  args: {
    slug: string;
    channelPublicId: string;
    messagePublicId: string;
    auth: { bearerToken: string };
    body: EditMessageRequest;
  },
) {
  return request.patch(
    `/api/t/${args.slug}/channels/${args.channelPublicId}/messages/${args.messagePublicId}`,
    {
      headers: { Authorization: `Bearer ${args.auth.bearerToken}` },
      data: args.body,
    },
  );
}

// ---------------------------------------------------------------------------
// /api/t/{slug}/channels/{channelPublicId}/messages/{messagePublicId} — DELETE
// ---------------------------------------------------------------------------

/**
 * DELETE /api/t/{slug}/channels/{channelPublicId}/messages/{messagePublicId}
 *
 * Deletes an existing message through the real delete endpoint.
 * Returns raw response. 204 = success, 403 = forbidden, 404 = not found.
 *
 * The caller (Owner) may delete any message in their tenant.
 */
export async function deleteMessage(
  request: APIRequestContext,
  args: {
    slug: string;
    channelPublicId: string;
    messagePublicId: string;
    auth: { bearerToken: string };
  },
) {
  return request.delete(
    `/api/t/${args.slug}/channels/${args.channelPublicId}/messages/${args.messagePublicId}`,
    {
      headers: { Authorization: `Bearer ${args.auth.bearerToken}` },
    },
  );
}

// ---------------------------------------------------------------------------
// Test-only namespace
// ---------------------------------------------------------------------------

export const testOnly = {
  /**
   * POST /api/t/{slug}/test-only/messages/inject-received
   *
   * Returns raw response. 201 = injected, 404 = test mode not active.
   */
  async injectReceived(
    request: APIRequestContext,
    args: {
      slug: string;
      auth: { bearerToken: string };
      body: TestInjectReceivedRequest;
    },
  ) {
    return request.post(
      `/api/t/${args.slug}/test-only/messages/inject-received`,
      {
        headers: { Authorization: `Bearer ${args.auth.bearerToken}` },
        data: args.body,
      },
    );
  },
};
