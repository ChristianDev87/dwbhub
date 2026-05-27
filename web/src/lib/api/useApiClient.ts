import { useMemo } from "react";
import { useAuth } from "@/app/auth-context";
import { createApiClient, type ApiClient } from "./client";

/**
 * React-hook wrapper around createApiClient that re-builds the client whenever
 * the access token changes. Components and TanStack-Query hooks should use
 * this hook instead of constructing the client themselves.
 */
export function useApiClient(): ApiClient {
  const { state } = useAuth();
  const accessToken = state.kind === "authenticated" ? state.accessToken : null;
  return useMemo(() => createApiClient(accessToken), [accessToken]);
}
