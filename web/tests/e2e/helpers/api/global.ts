/**
 * Typed API helpers for global (non-tenant-scoped) endpoints.
 *
 * Mirrors the .NET DwbHub.Tests.Shared pattern: each function wraps a single
 * Playwright APIRequestContext call with typed args and return value derived
 * from the generated paths interface.
 *
 * Mailpit endpoints (/api/v1/messages, /api/v1/message/{id}) are intentionally
 * NOT covered here — they are a third-party service with its own API shape.
 */

import type { APIRequestContext } from "@playwright/test";
import type { paths } from "../../../../src/lib/api/generated/schema";

// ---------------------------------------------------------------------------
// /api/health
// ---------------------------------------------------------------------------

type HealthResponse =
  paths["/api/health"]["get"]["responses"]["200"]["content"]["application/json"];

/**
 * GET /api/health
 *
 * Returns the health/version payload. Throws on non-2xx.
 */
export async function getHealth(
  request: APIRequestContext,
): Promise<HealthResponse> {
  const res = await request.get("/api/health");
  if (!res.ok()) throw new Error(`GET /api/health ${res.status()}`);
  return (await res.json()) as HealthResponse;
}

// ---------------------------------------------------------------------------
// /api/setup/status
// ---------------------------------------------------------------------------

type SetupStatusResponse =
  paths["/api/setup/status"]["get"]["responses"]["200"]["content"]["application/json"];

/**
 * GET /api/setup/status
 *
 * Returns { completed: boolean }. Does NOT throw on non-2xx (callers like
 * isSetupComplete() need to handle network failures gracefully) — returns
 * null on failure instead.
 */
export async function getSetupStatus(
  request: APIRequestContext,
): Promise<SetupStatusResponse | null> {
  try {
    const res = await request.get("/api/setup/status");
    if (!res.ok()) return null;
    return (await res.json()) as SetupStatusResponse;
  } catch {
    return null;
  }
}

/**
 * GET /api/setup/status — throwing variant.
 *
 * Throws when the response is not 2xx. Used in ensureSetupCompleted().
 */
export async function getSetupStatusOrThrow(
  request: APIRequestContext,
): Promise<SetupStatusResponse> {
  const res = await request.get("/api/setup/status");
  if (!res.ok()) {
    throw new Error(
      `GET /api/setup/status returned ${res.status()} — api unreachable from e2e container?`,
    );
  }
  return (await res.json()) as SetupStatusResponse;
}

// ---------------------------------------------------------------------------
// /api/setup/complete
// ---------------------------------------------------------------------------

type SetupCompleteRequest = NonNullable<
  paths["/api/setup/complete"]["post"]["requestBody"]
>["content"]["application/json"];

/**
 * POST /api/setup/complete
 *
 * Returns the raw Playwright response so callers can inspect status codes
 * (200, 410 Gone = already completed, 4xx = error).
 */
export async function postSetupComplete(
  request: APIRequestContext,
  body: SetupCompleteRequest,
) {
  return request.post("/api/setup/complete", { data: body });
}

// ---------------------------------------------------------------------------
// /api/auth/verify-email/confirm
// ---------------------------------------------------------------------------

type VerifyEmailConfirmRequest = NonNullable<
  paths["/api/auth/verify-email/confirm"]["post"]["requestBody"]
>["content"]["application/json"];

/**
 * POST /api/auth/verify-email/confirm
 *
 * Returns raw response so the caller can inspect the status.
 */
export async function postVerifyEmailConfirm(
  request: APIRequestContext,
  body: VerifyEmailConfirmRequest,
) {
  return request.post("/api/auth/verify-email/confirm", { data: body });
}
