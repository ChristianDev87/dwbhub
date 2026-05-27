/**
 * Typed API helpers for tenant-auth endpoints.
 *
 * Covers /api/tenants/{slug}/auth/login which is used in bootstrap helpers
 * and spec-local apiLogin() functions.
 */

import type { APIRequestContext } from "@playwright/test";
import type { paths } from "../../../../src/lib/api/generated/schema";

// ---------------------------------------------------------------------------
// /api/tenants/{slug}/auth/login
// ---------------------------------------------------------------------------

type TenantLoginRequest = NonNullable<
  paths["/api/tenants/{slug}/auth/login"]["post"]["requestBody"]
>["content"]["application/json"];

/**
 * POST /api/tenants/{slug}/auth/login
 *
 * Returns the raw Playwright response. Callers decide whether to throw or
 * inspect the status (bootstrap code has specific error messages, spec
 * helpers just extract accessToken).
 */
export async function postTenantLogin(
  request: APIRequestContext,
  args: { slug: string; body: TenantLoginRequest },
) {
  return request.post(`/api/tenants/${args.slug}/auth/login`, {
    data: args.body,
  });
}
