/**
 * Typed API helpers for guild endpoints.
 *
 * Covers /api/t/{slug}/guilds[/{publicId}], /activate, /deactivate,
 * /bot/reconnect, /bot-credentials, and /channels/sync.
 *
 * Symmetric to DwbHub.Tests.Shared.Api.GuildsApi + GuildBotApi.
 */

import type { APIRequestContext } from "@playwright/test";
import type { paths } from "../../../../src/lib/api/generated/schema";

// ---------------------------------------------------------------------------
// Types derived from paths
// ---------------------------------------------------------------------------

type GuildListResponse =
  paths["/api/t/{slug}/guilds"]["get"]["responses"]["200"]["content"]["application/json"];

type AddGuildRequest = NonNullable<
  paths["/api/t/{slug}/guilds"]["post"]["requestBody"]
>["content"]["application/json"];

type PutBotCredentialsRequest = NonNullable<
  paths["/api/t/{slug}/guilds/{guildPublicId}/bot-credentials"]["put"]["requestBody"]
>["content"]["application/json"];

// ---------------------------------------------------------------------------
// /api/t/{slug}/guilds — list
// ---------------------------------------------------------------------------

/**
 * GET /api/t/{slug}/guilds
 *
 * Returns raw response so callers can inspect status and body.
 */
export async function listGuilds(
  request: APIRequestContext,
  args: { slug: string; auth: { bearerToken: string } },
): Promise<ReturnType<typeof request.get>> {
  return request.get(`/api/t/${args.slug}/guilds`, {
    headers: { Authorization: `Bearer ${args.auth.bearerToken}` },
  });
}

/**
 * GET /api/t/{slug}/guilds — typed body variant.
 *
 * Throws on non-2xx. Returns parsed JSON body.
 */
export async function listGuildsOrThrow(
  request: APIRequestContext,
  args: { slug: string; auth: { bearerToken: string } },
): Promise<GuildListResponse> {
  const res = await listGuilds(request, args);
  if (!res.ok()) {
    throw new Error(`GET /api/t/${args.slug}/guilds ${res.status()}`);
  }
  return (await res.json()) as GuildListResponse;
}

// ---------------------------------------------------------------------------
// /api/t/{slug}/guilds — create
// ---------------------------------------------------------------------------

/**
 * POST /api/t/{slug}/guilds
 *
 * Returns raw response. Callers inspect status (201 = created, 409 = conflict).
 */
export async function createGuild(
  request: APIRequestContext,
  args: {
    slug: string;
    auth: { bearerToken: string };
    body: AddGuildRequest;
  },
) {
  return request.post(`/api/t/${args.slug}/guilds`, {
    headers: { Authorization: `Bearer ${args.auth.bearerToken}` },
    data: args.body,
  });
}

// ---------------------------------------------------------------------------
// /api/t/{slug}/guilds/{publicId}/bot-credentials — upsert
// ---------------------------------------------------------------------------

/**
 * PUT /api/t/{slug}/guilds/{guildPublicId}/bot-credentials
 *
 * Returns raw response. 204 = success, 404 = guild not found.
 */
export async function putBotCredentials(
  request: APIRequestContext,
  args: {
    slug: string;
    guildPublicId: string;
    auth: { bearerToken: string };
    body: PutBotCredentialsRequest;
  },
) {
  return request.put(
    `/api/t/${args.slug}/guilds/${args.guildPublicId}/bot-credentials`,
    {
      headers: { Authorization: `Bearer ${args.auth.bearerToken}` },
      data: args.body,
    },
  );
}

// ---------------------------------------------------------------------------
// /api/t/{slug}/guilds/{publicId}/activate
// ---------------------------------------------------------------------------

/**
 * POST /api/t/{slug}/guilds/{publicId}/activate
 *
 * Returns raw response. 204 = success.
 */
export async function activateGuild(
  request: APIRequestContext,
  args: {
    slug: string;
    publicId: string;
    auth: { bearerToken: string };
  },
) {
  return request.post(`/api/t/${args.slug}/guilds/${args.publicId}/activate`, {
    headers: { Authorization: `Bearer ${args.auth.bearerToken}` },
  });
}

// ---------------------------------------------------------------------------
// /api/t/{slug}/guilds/{guildPublicId}/channels/sync
// ---------------------------------------------------------------------------

/**
 * POST /api/t/{slug}/guilds/{guildPublicId}/channels/sync
 *
 * Returns raw response. 204 = success, 404 = guild not found.
 */
export async function syncChannels(
  request: APIRequestContext,
  args: {
    slug: string;
    guildPublicId: string;
    auth: { bearerToken: string };
  },
) {
  return request.post(
    `/api/t/${args.slug}/guilds/${args.guildPublicId}/channels/sync`,
    {
      headers: { Authorization: `Bearer ${args.auth.bearerToken}` },
    },
  );
}

// ---------------------------------------------------------------------------
// /api/t/{slug}/guilds/{guildPublicId}/channels — list (guild-scoped)
// ---------------------------------------------------------------------------

type ChannelListResponse =
  paths["/api/t/{slug}/guilds/{guildPublicId}/channels"]["get"]["responses"]["200"]["content"]["application/json"];

/**
 * GET /api/t/{slug}/guilds/{guildPublicId}/channels
 *
 * Returns raw response. Use listGuildChannelsOrThrow for the typed body.
 */
export async function listGuildChannels(
  request: APIRequestContext,
  args: {
    slug: string;
    guildPublicId: string;
    auth: { bearerToken: string };
  },
) {
  return request.get(
    `/api/t/${args.slug}/guilds/${args.guildPublicId}/channels`,
    {
      headers: { Authorization: `Bearer ${args.auth.bearerToken}` },
    },
  );
}

/**
 * GET /api/t/{slug}/guilds/{guildPublicId}/channels — typed body variant.
 *
 * Throws on non-2xx.
 */
export async function listGuildChannelsOrThrow(
  request: APIRequestContext,
  args: {
    slug: string;
    guildPublicId: string;
    auth: { bearerToken: string };
  },
): Promise<ChannelListResponse> {
  const res = await listGuildChannels(request, args);
  if (!res.ok()) {
    throw new Error(
      `GET /api/t/${args.slug}/guilds/${args.guildPublicId}/channels ${res.status()}`,
    );
  }
  return (await res.json()) as ChannelListResponse;
}
