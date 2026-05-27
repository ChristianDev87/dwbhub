/**
 * Typed API helpers for message endpoints.
 *
 * Covers /api/t/{slug}/channels/{channelPublicId}/messages (GET + POST)
 * and the test-only namespace:
 *   /api/t/{slug}/test-only/messages/{messageId}/edit
 *   /api/t/{slug}/test-only/messages/{messageId}/delete
 *   /api/t/{slug}/test-only/messages/inject-received
 *
 * Symmetric to DwbHub.Tests.Shared.Api.MessagesApi.
 */

import type { APIRequestContext } from "@playwright/test";
import type { paths } from "../../../../src/lib/api/generated/schema";

// ---------------------------------------------------------------------------
// Types derived from paths
// ---------------------------------------------------------------------------

type TestEditRequest = NonNullable<
  paths["/api/t/{slug}/test-only/messages/{messageId}/edit"]["post"]["requestBody"]
>["content"]["application/json"];

type TestInjectReceivedRequest = NonNullable<
  paths["/api/t/{slug}/test-only/messages/inject-received"]["post"]["requestBody"]
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
// Test-only namespace
// ---------------------------------------------------------------------------

export const testOnly = {
  /**
   * POST /api/t/{slug}/test-only/messages/{messageId}/edit
   *
   * Returns raw response. 204 = success, 404 = test mode not active.
   */
  async editMessage(
    request: APIRequestContext,
    args: {
      slug: string;
      messageId: string | number;
      auth: { bearerToken: string };
      body: TestEditRequest;
    },
  ) {
    return request.post(
      `/api/t/${args.slug}/test-only/messages/${args.messageId}/edit`,
      {
        headers: { Authorization: `Bearer ${args.auth.bearerToken}` },
        data: args.body,
      },
    );
  },

  /**
   * POST /api/t/{slug}/test-only/messages/{messageId}/delete
   *
   * Returns raw response. 204 = success, 404 = test mode not active.
   */
  async deleteMessage(
    request: APIRequestContext,
    args: {
      slug: string;
      messageId: string | number;
      auth: { bearerToken: string };
    },
  ) {
    return request.post(
      `/api/t/${args.slug}/test-only/messages/${args.messageId}/delete`,
      {
        headers: { Authorization: `Bearer ${args.auth.bearerToken}` },
      },
    );
  },

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
