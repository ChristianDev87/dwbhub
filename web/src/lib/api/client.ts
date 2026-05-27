import createClient from "openapi-fetch";
import type { Middleware } from "openapi-fetch";
import type { paths } from "./generated/schema";

/**
 * Build a typed openapi-fetch client bound to the current access token.
 *
 * The token is captured by closure inside an auth middleware so that the
 * client itself stays a plain createClient<paths>() result — no hidden state.
 *
 * Same-origin baseUrl ("/") so the Vite dev proxy ({@link ../../../vite.config.ts})
 * forwards /api/* to the backend in dev and the production deploy serves both
 * SPA and API from the same host.
 */
export function createApiClient(accessToken: string | null, baseUrl = "/") {
  const client = createClient<paths>({ baseUrl });

  const authMiddleware: Middleware = {
    async onRequest({ request }) {
      if (accessToken !== null) {
        request.headers.set("Authorization", `Bearer ${accessToken}`);
      }
      return request;
    },
  };

  client.use(authMiddleware);
  return client;
}

export type ApiClient = ReturnType<typeof createApiClient>;
