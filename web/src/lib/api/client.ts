import createClient from "openapi-fetch";
import type { Middleware } from "openapi-fetch";
import type { paths } from "./generated/schema";

/**
 * Module-level in-flight refresh promise, shared across all client instances
 * created in the same JS module scope.
 *
 * When multiple requests receive a 401 simultaneously, only the first one
 * triggers `refreshAccessToken()`. All subsequent 401 handlers await the same
 * promise (Promise-coalescing). This prevents redundant refresh calls and race
 * conditions where two refreshes would invalidate each other's tokens.
 */
let inflightRefresh: Promise<string | null> | null = null;

/**
 * Build a typed openapi-fetch client with automatic token injection and
 * transparent 401-retry via a token-refresh flow.
 *
 * @param getAccessToken   Stable function that returns the current access token
 *                         (or null when unauthenticated). Called fresh for each
 *                         outgoing request so token rotations are picked up
 *                         immediately without rebuilding the client.
 * @param refreshAccessToken  Async function that calls /api/auth/refresh and
 *                         returns the new access token on success, or null on
 *                         failure. Must be a raw fetch call — routing this
 *                         through the typed client would risk an infinite
 *                         refresh loop. On failure the AuthContext is
 *                         responsible for setting state to "unauthenticated".
 * @param baseUrl          Same-origin base URL. Defaults to "/" so the Vite
 *                         dev proxy forwards /api/* to the backend in dev and
 *                         the production deploy serves both SPA and API from
 *                         the same host.
 */
export function createApiClient(
  getAccessToken: () => string | null,
  refreshAccessToken: () => Promise<string | null>,
  baseUrl = "/",
) {
  const client = createClient<paths>({ baseUrl });

  /**
   * Per-request clone storage used to work around the consumed-body problem:
   * by the time `onResponse` fires, the original request body may already have
   * been read by the fetch infrastructure. We store a clone in `onRequest` and
   * use it for the retry in `onResponse`.
   *
   * WeakMap keyed on the Request object keeps memory bounded — entries are
   * automatically GC'd when the Request is no longer referenced.
   */
  const requestClones = new WeakMap<Request, Request>();

  const authMiddleware: Middleware = {
    /**
     * Inject the current Bearer token and store a clone of the request so the
     * body is available for a potential retry in `onResponse`.
     */
    async onRequest({ request }) {
      // Store clone before the body is consumed by the actual fetch.
      requestClones.set(request, request.clone());

      const token = getAccessToken();
      if (token !== null) {
        request.headers.set("Authorization", `Bearer ${token}`);
      }
      return request;
    },

    /**
     * On a 401 response:
     *  1. Skip if the request already carries the loop-guard header
     *     (X-Refresh-Retried), preventing an infinite refresh cycle.
     *  2. Coalesce concurrent 401s into a single `refreshAccessToken()` call
     *     via `inflightRefresh`. All waiters receive the same token.
     *  3. On successful refresh, retry the original request with the new token
     *     and the loop-guard header set.
     *  4. On failed refresh (null), return the original 401 so the calling
     *     component can react. The AuthContext will separately set state to
     *     "unauthenticated" as a side-effect of the failed refresh.
     */
    async onResponse({ request, response }) {
      if (response.status !== 401) return response;

      // Loop guard: if this request already retried once, pass the 401 through.
      if (request.headers.has("X-Refresh-Retried")) return response;

      // Promise-coalescing: only one refresh call at a time across all clients.
      if (inflightRefresh === null) {
        inflightRefresh = refreshAccessToken().finally(() => {
          inflightRefresh = null;
        });
      }
      const newToken = await inflightRefresh;

      if (newToken === null) {
        // Refresh failed — AuthContext handles the logout side-effect.
        // Return the 401 so components know the request was not authorised.
        return response;
      }

      // Retrieve the cloned request (body not yet consumed).
      const cloned = requestClones.get(request) ?? request.clone();

      const retryHeaders = new Headers(cloned.headers);
      retryHeaders.set("Authorization", `Bearer ${newToken}`);
      retryHeaders.set("X-Refresh-Retried", "1");

      return fetch(cloned.url, {
        method: cloned.method,
        headers: retryHeaders,
        body: cloned.body,
        credentials: cloned.credentials,
        mode: cloned.mode,
        cache: cloned.cache,
        redirect: cloned.redirect,
        referrerPolicy: cloned.referrerPolicy,
      });
    },
  };

  client.use(authMiddleware);
  return client;
}

export type ApiClient = ReturnType<typeof createApiClient>;
