import { useCallback, useMemo, useRef } from "react";
import { useAuth } from "@/app/auth-context";
import { createApiClient, type ApiClient } from "./client";

/**
 * React-hook wrapper around createApiClient that wires the auth context into
 * the 401-refresh middleware without rebuilding the client on every render.
 *
 * Strategy:
 *  - A ref tracks the current access token so `getAccessToken` always returns
 *    the latest value without being listed as a useMemo dependency (avoids
 *    rebuilding the client on every token rotation).
 *  - `refreshAccessToken` is consumed directly from AuthContext where it is
 *    already a stable useCallback reference, so it is safe to use as a
 *    useMemo dependency.
 *  - The client is only rebuilt when `refreshAccessToken` identity changes
 *    (practically: never after the initial mount).
 */
export function useApiClient(): ApiClient {
  const { state, refreshAccessToken } = useAuth();

  // Keep the current token in a ref so getAccessToken is always fresh,
  // but the ref object itself never changes — stable identity for useMemo.
  const tokenRef = useRef<string | null>(null);
  tokenRef.current = state.kind === "authenticated" ? state.accessToken : null;

  // Stable getter — reads through the ref, no deps needed.
  const getAccessToken = useCallback(() => tokenRef.current, []);

  return useMemo(
    () => createApiClient(getAccessToken, refreshAccessToken),
    [getAccessToken, refreshAccessToken],
  );
}
