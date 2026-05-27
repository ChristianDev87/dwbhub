/**
 * Typed API helpers for channel-scoped endpoints.
 *
 * Covers /api/t/{slug}/channels/{channelPublicId}/bridge,
 * /backfill-status, and /backfill-jobs/{jobId}.
 *
 * Symmetric to DwbHub.Tests.Shared.Api.ChannelsApi.
 */

import type { APIRequestContext } from "@playwright/test";
import type { paths } from "../../../../src/lib/api/generated/schema";

// ---------------------------------------------------------------------------
// Types derived from paths
// ---------------------------------------------------------------------------

type BackfillStatusResponse =
  paths["/api/t/{slug}/channels/{channelPublicId}/backfill-status"]["get"]["responses"]["200"]["content"]["application/json"];

// ---------------------------------------------------------------------------
// /api/t/{slug}/channels/{channelPublicId}/bridge — POST (bridge)
// ---------------------------------------------------------------------------

/**
 * POST /api/t/{slug}/channels/{channelPublicId}/bridge
 *
 * Returns raw response. Expected statuses: 202 = bridged, 409 = already bridged.
 * Callers must check status themselves (e.g. expect([202, 409]).toContain(...)).
 */
export async function bridgeChannel(
  request: APIRequestContext,
  args: {
    slug: string;
    channelPublicId: string;
    auth: { bearerToken: string };
  },
) {
  return request.post(
    `/api/t/${args.slug}/channels/${args.channelPublicId}/bridge`,
    {
      headers: { Authorization: `Bearer ${args.auth.bearerToken}` },
    },
  );
}

// ---------------------------------------------------------------------------
// /api/t/{slug}/channels/{channelPublicId}/bridge — DELETE (unbridge)
// ---------------------------------------------------------------------------

/**
 * DELETE /api/t/{slug}/channels/{channelPublicId}/bridge
 *
 * Returns raw response. 204 = unbridged.
 */
export async function unbridgeChannel(
  request: APIRequestContext,
  args: {
    slug: string;
    channelPublicId: string;
    auth: { bearerToken: string };
  },
) {
  return request.delete(
    `/api/t/${args.slug}/channels/${args.channelPublicId}/bridge`,
    {
      headers: { Authorization: `Bearer ${args.auth.bearerToken}` },
    },
  );
}

// ---------------------------------------------------------------------------
// /api/t/{slug}/channels/{channelPublicId}/backfill-status — GET
// ---------------------------------------------------------------------------

/**
 * GET /api/t/{slug}/channels/{channelPublicId}/backfill-status
 *
 * Returns raw response. Callers check res.ok() and parse body themselves
 * during polling loops (where a non-ok mid-poll is not fatal).
 */
export async function getBackfillStatus(
  request: APIRequestContext,
  args: {
    slug: string;
    channelPublicId: string;
    auth: { bearerToken: string };
  },
) {
  return request.get(
    `/api/t/${args.slug}/channels/${args.channelPublicId}/backfill-status`,
    {
      headers: { Authorization: `Bearer ${args.auth.bearerToken}` },
    },
  );
}

/**
 * GET /api/t/{slug}/channels/{channelPublicId}/backfill-status — typed body variant.
 *
 * Throws on non-2xx. Use this when you need the parsed body outside a polling loop.
 */
export async function getBackfillStatusOrThrow(
  request: APIRequestContext,
  args: {
    slug: string;
    channelPublicId: string;
    auth: { bearerToken: string };
  },
): Promise<BackfillStatusResponse> {
  const res = await getBackfillStatus(request, args);
  if (!res.ok()) {
    throw new Error(
      `GET /api/t/${args.slug}/channels/${args.channelPublicId}/backfill-status ${res.status()}`,
    );
  }
  return (await res.json()) as BackfillStatusResponse;
}
