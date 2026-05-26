import { readFileSync } from "node:fs";
import type { APIRequestContext } from "@playwright/test";

// ---------------------------------------------------------------------------
// Fake Discord bot token — valid shape per BotTokenShapeAttribute:
//   - Exactly 2 dots (3 segments)
//   - At least 30 alphanumeric chars total
//   - Matches ^[A-Za-z0-9._-]{50,200}$
// This token will never reach the real Discord API because the stack uses
// FakeDiscordRestChannelClient when DWBHUB_DISCORD_TEST_MODE=fake-rest.
// ---------------------------------------------------------------------------
const FAKE_BOT_TOKEN =
  "MTAwMDAwMDAwMDAwMDAwMDAwMDAwMDA.GFakeToken.AbCdEfGhIjKlMnOpQrStUvWxYzAb-fake";

// Deterministic fake Discord guild snowflake used by seedActiveGuild.
// The value is stable across runs so the guild upsert is idempotent IF the
// database volume is preserved between test sessions. Individual test cases
// that need isolation should generate their own unique IDs.
const FAKE_GUILD_DISCORD_ID = "100200300400500600";

const BOOTSTRAP_TOKEN_PATH = "/api-bootstrap-ro/bootstrap-token.txt";

/**
 * Seed data used when bootstrapping a fresh stack for e2e runs.
 *
 * These values match what every existing spec already hard-codes — they are
 * centralised here so they can be imported instead of re-stated per file.
 */
export const SETUP_DEFAULTS = {
  tenantSlug: "acme",
  tenantName: "ACME Corporation",
  tenantLocale: "en" as const,
  ownerEmail: "owner@acme.test",
  ownerDisplayName: "ACME Owner",
  ownerPassword: "correct horse battery staple",
} as const;

// ---------------------------------------------------------------------------
// Internal helpers
// ---------------------------------------------------------------------------

/**
 * Resolve the Mailpit REST API base URL from the environment.
 *
 * e2e.sh exports `MAILHOG_UI_PORT=18025` before running Playwright so this
 * value is always available inside the e2e-tests container. A fallback is
 * provided for the rare case of a local dev run against a default compose stack.
 */
function mailpitBaseUrl(): string {
  const port = process.env["MAILHOG_UI_PORT"] ?? "18025";
  return `http://host.docker.internal:${port}`;
}

interface MailpitMessage {
  ID: string;
  Subject: string;
}
interface MailpitMessagesResponse {
  messages: MailpitMessage[];
  total: number;
}
interface MailpitMessageDetail {
  ID: string;
  Subject: string;
  HTML: string;
  Text: string;
}

/**
 * Poll Mailpit until at least one email addressed to `ownerEmail` arrives,
 * then extract the verification token from the HTML body.
 *
 * The email template embeds the token as a query-string parameter:
 *   …/verify-email?token=<urlencoded-token>
 * This mirrors how DwbHub.Tests.Integration.Auth.EmailVerificationConfirmTests
 * extracts the token from Mailpit in the backend integration test suite.
 */
async function extractVerificationToken(ownerEmail: string): Promise<string> {
  const base = mailpitBaseUrl();
  const maxWaitMs = 15_000;
  const pollIntervalMs = 500;
  const start = Date.now();

  while (Date.now() - start < maxWaitMs) {
    const res = await fetch(`${base}/api/v1/messages`);
    if (!res.ok) {
      throw new Error(
        `Mailpit /api/v1/messages returned ${res.status} — is the dev stack running?`,
      );
    }
    const data = (await res.json()) as MailpitMessagesResponse;
    const messages = data.messages ?? [];
    if (messages.length > 0) {
      // Take the most recent message — on a fresh volume there will be exactly one.
      const msgId = messages[0]!.ID;
      const detail: MailpitMessageDetail = (await (
        await fetch(`${base}/api/v1/message/${msgId}`)
      ).json()) as MailpitMessageDetail;

      const match = /[?&]token=([A-Za-z0-9_\-%]+)/.exec(detail.HTML);
      if (match?.[1] !== undefined) {
        return decodeURIComponent(match[1]);
      }
      throw new Error(
        `Mailpit message found but no verification token in HTML body.\n` +
          `Subject: ${detail.Subject}\n` +
          `HTML snippet: ${detail.HTML.slice(0, 300)}`,
      );
    }
    await new Promise((r) => setTimeout(r, pollIntervalMs));
  }

  throw new Error(
    `No email arrived in Mailpit within ${maxWaitMs}ms after bootstrap setup.\n` +
      `Mailpit URL: ${base}\n` +
      `Expected recipient: ${ownerEmail}`,
  );
}

// ---------------------------------------------------------------------------
// Public API
// ---------------------------------------------------------------------------

/**
 * Ensure tenant setup is completed and the owner account is email-verified
 * before tests that depend on an authenticated user.
 *
 * Idempotent: returns immediately if `/api/setup/status` reports completed=true.
 *
 * Otherwise:
 * 1. Reads the per-stack bootstrap token from the api-data volume (mounted
 *    read-only at /api-bootstrap-ro by docker-compose.test.yml Task 4).
 * 2. POSTs it to /api/setup/complete via the real owner-bootstrap flow — the
 *    same path a production operator would use, with no test-only endpoint.
 * 3. Fetches the verification email from Mailpit and confirms it via
 *    /api/auth/verify-email/confirm so the owner account is loginnable.
 *
 * **Throws** on any failure (status endpoint not reachable, token file missing,
 * POST /api/setup/complete non-2xx, email never arrived, confirm non-2xx).
 * Silent skipping is the pattern Plan 0.8.4 is removing; setup failures must
 * surface loudly so missing coverage is noticed immediately.
 *
 * @param request  Playwright APIRequestContext (from `{ request }` fixture).
 *                 Must have the Vite dev-server URL as its baseURL so that
 *                 /api/* calls are proxied through to the backend.
 */
export async function ensureSetupCompleted(
  request: APIRequestContext,
): Promise<void> {
  // 1. Check current setup status.
  const statusRes = await request.get("/api/setup/status");
  if (!statusRes.ok()) {
    throw new Error(
      `GET /api/setup/status returned ${statusRes.status()} — api unreachable from e2e container?`,
    );
  }
  const status = (await statusRes.json()) as { completed: boolean };
  if (status.completed) {
    // Already completed on a prior run — api-data volume was reused.
    return;
  }

  // 2. Read bootstrap token from the api-data volume (mounted :ro at /api-bootstrap-ro).
  let bootstrapToken: string;
  try {
    bootstrapToken = readFileSync(BOOTSTRAP_TOKEN_PATH, "utf8").trim();
  } catch (err) {
    throw new Error(
      `Bootstrap token not readable at ${BOOTSTRAP_TOKEN_PATH}: ${(err as Error).message}\n` +
        `Verify docker-compose.test.yml mounts api-data:/api-bootstrap-ro:ro on the e2e-tests service.`,
    );
  }

  // 3. POST /api/setup/complete — token goes in the request body, not a header.
  //    Field names match SetupCompleteRequest (camelCase .NET default serialisation):
  //    bootstrapToken, tenantName, tenantSlug, tenantLocale, ownerEmail,
  //    ownerDisplayName, ownerPassword.
  const setupRes = await request.post("/api/setup/complete", {
    data: {
      bootstrapToken,
      tenantName: SETUP_DEFAULTS.tenantName,
      tenantSlug: SETUP_DEFAULTS.tenantSlug,
      tenantLocale: SETUP_DEFAULTS.tenantLocale,
      ownerEmail: SETUP_DEFAULTS.ownerEmail,
      ownerDisplayName: SETUP_DEFAULTS.ownerDisplayName,
      ownerPassword: SETUP_DEFAULTS.ownerPassword,
    },
  });

  // 410 Gone = already completed (race or stale lock) — treat as success.
  if (setupRes.status() === 410) return;

  if (!setupRes.ok()) {
    const body = await setupRes.text();
    throw new Error(
      `POST /api/setup/complete failed with ${setupRes.status()}: ${body.slice(0, 400)}`,
    );
  }

  // 4. The setup flow creates the owner with email_verified_at = null.
  //    Login requires verification (Plan 0.3c gate). Fetch the email from
  //    Mailpit and confirm it so all login-dependent specs can proceed.
  const verifyToken = await extractVerificationToken(SETUP_DEFAULTS.ownerEmail);

  const confirmRes = await request.post("/api/auth/verify-email/confirm", {
    data: { token: verifyToken },
  });

  if (!confirmRes.ok()) {
    const body = await confirmRes.text();
    throw new Error(
      `POST /api/auth/verify-email/confirm failed with ${confirmRes.status()}: ${body.slice(0, 400)}`,
    );
  }
}

// ---------------------------------------------------------------------------
// seedActiveGuild
// ---------------------------------------------------------------------------

export interface SeedGuildResult {
  guildPublicId: string;
  /** First text channel publicId returned by the fake client after sync */
  firstTextChannelPublicId: string | null;
}

/**
 * Login as the tenant owner, create a Discord guild record, configure a fake
 * bot token, activate the bot connection, and sync channels from the fake
 * Discord REST client.
 *
 * Prerequisites:
 *   - ensureSetupCompleted() must have been called first (or the stack must
 *     already be bootstrapped).
 *   - DWBHUB_DISCORD_TEST_MODE=fake-rest must be active on the API container
 *     so the channel sync succeeds without real Discord credentials.
 *
 * Returns { guildPublicId, firstTextChannelPublicId } for use in test assertions.
 *
 * If a guild with `discordGuildId` already exists (HTTP 409), the function
 * treats it as idempotent and fetches the existing guild list to return the
 * matching publicId.
 */
export async function seedActiveGuild(
  request: APIRequestContext,
  opts?: {
    discordGuildId?: string;
    displayName?: string;
  },
): Promise<SeedGuildResult> {
  const slug = SETUP_DEFAULTS.tenantSlug;
  const discordGuildId = opts?.discordGuildId ?? FAKE_GUILD_DISCORD_ID;
  const displayName = opts?.displayName ?? "E2E Fake Guild";

  // 1. Login as owner to get access token (cookie-based sessions).
  // The login endpoint is /api/tenants/{slug}/auth/login (tenant-scoped).
  const loginRes = await request.post(`/api/tenants/${slug}/auth/login`, {
    data: {
      email: SETUP_DEFAULTS.ownerEmail,
      password: SETUP_DEFAULTS.ownerPassword,
    },
  });
  if (!loginRes.ok()) {
    const body = await loginRes.text();
    throw new Error(
      `seedActiveGuild: login failed with ${loginRes.status()}: ${body.slice(0, 400)}`,
    );
  }
  const loginBody = (await loginRes.json()) as { accessToken: string };
  const accessToken = loginBody.accessToken;
  const authHeader = { Authorization: `Bearer ${accessToken}` };

  // 2. Add guild (idempotent — 409 treated as success).
  let guildPublicId: string;

  const addRes = await request.post(`/api/t/${slug}/guilds`, {
    headers: authHeader,
    data: { discordGuildId, displayName },
  });

  if (addRes.ok()) {
    const body = (await addRes.json()) as { publicId: string };
    guildPublicId = body.publicId;
  } else if (addRes.status() === 409) {
    // Already exists — find it in the list
    const listRes = await request.get(`/api/t/${slug}/guilds`, {
      headers: authHeader,
    });
    if (!listRes.ok()) {
      throw new Error(
        `seedActiveGuild: GET guilds failed ${listRes.status()} after 409 on add`,
      );
    }
    const listBody = (await listRes.json()) as {
      guilds: Array<{ publicId: string; discordGuildId: string }>;
    };
    const existing = listBody.guilds.find(
      (g) => g.discordGuildId === discordGuildId,
    );
    if (!existing) {
      throw new Error(
        `seedActiveGuild: got 409 but could not find guild ${discordGuildId} in list`,
      );
    }
    guildPublicId = existing.publicId;
  } else {
    const body = await addRes.text();
    throw new Error(
      `seedActiveGuild: POST guilds failed with ${addRes.status()}: ${body.slice(0, 400)}`,
    );
  }

  // 3. PUT bot credentials (idempotent upsert).
  const credRes = await request.put(
    `/api/t/${slug}/guilds/${guildPublicId}/bot-credentials`,
    {
      headers: authHeader,
      data: { token: FAKE_BOT_TOKEN },
    },
  );
  // 204 = set; 404 = guild not found (shouldn't happen at this point)
  if (!credRes.ok()) {
    const body = await credRes.text();
    throw new Error(
      `seedActiveGuild: PUT bot-credentials failed ${credRes.status()}: ${body.slice(0, 300)}`,
    );
  }

  // 4. Activate the bot connection (idempotent — guild is already active by default).
  //    The GuildsController.Activate route is POST /api/t/{slug}/guilds/{publicId}/activate.
  //    New guilds are active=true on creation, so this is a no-op — but we call it
  //    to ensure OnGuildActivatedAsync runs and the BotConnectionManager picks up creds.
  await request.post(`/api/t/${slug}/guilds/${guildPublicId}/activate`, {
    headers: authHeader,
  });

  // Brief pause to allow BotConnectionManager to process credentials asynchronously.
  await new Promise((r) => setTimeout(r, 800));

  // 5. Sync channels from FakeDiscordRestChannelClient.
  const syncRes = await request.post(
    `/api/t/${slug}/guilds/${guildPublicId}/channels/sync`,
    { headers: authHeader },
  );
  if (!syncRes.ok() && syncRes.status() !== 204) {
    const body = await syncRes.text();
    throw new Error(
      `seedActiveGuild: POST channels/sync failed ${syncRes.status()}: ${body.slice(0, 400)}`,
    );
  }

  // 6. Fetch channel list to find the first text channel public ID.
  const channelRes = await request.get(
    `/api/t/${slug}/guilds/${guildPublicId}/channels`,
    { headers: authHeader },
  );
  if (!channelRes.ok()) {
    throw new Error(
      `seedActiveGuild: GET channels failed ${channelRes.status()}`,
    );
  }
  const channelBody = (await channelRes.json()) as {
    channels: Array<{ publicId: string; channelType: number }>;
  };
  const firstText =
    channelBody.channels.find((c) => c.channelType === 0)?.publicId ?? null;

  return { guildPublicId, firstTextChannelPublicId: firstText };
}
