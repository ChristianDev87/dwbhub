import { createContext, useContext } from "react";

/**
 * Auth-related types, the React context object, and the `useAuth` accessor hook.
 *
 * Lives in its own module (no JSX) so the matching `AuthContext.tsx` file can
 * stay a pure component module — required by the `react-refresh/only-export-components`
 * ESLint rule, which trips when a component file also exports non-components
 * (types, hooks, plain functions, etc.).
 *
 * Imported by:
 *   - app/AuthContext.tsx (the AuthProvider component) — uses `AuthContext`
 *     (the React context object) + `AuthContextValue` for the provider value.
 *   - any component or hook that needs `useAuth()` to read the current auth state.
 */

type User = {
  id: number;
  email: string;
  displayName: string;
  role: string;
};

type Tenant = {
  id: number;
  slug: string;
  name: string;
  locale?: "de" | "en";
  /** Per-tenant edit-window override in seconds; null means system default. */
  messageEditWindowSeconds?: number | null;
};

export type AuthState =
  | { kind: "checking" }
  | { kind: "unauthenticated" }
  | {
      kind: "authenticated";
      accessToken: string;
      user: User;
      tenant: Tenant;
    };

export type LoginResult =
  | { kind: "success" }
  | { kind: "invalid_credentials" }
  | { kind: "locked_out"; retryAfterSeconds: number }
  | { kind: "email_not_verified"; email: string }
  | { kind: "network_error" };

export interface AuthContextValue {
  state: AuthState;
  login: (
    slug: string,
    email: string,
    password: string,
  ) => Promise<LoginResult>;
  logout: () => Promise<void>;
  /**
   * Attempts a cookie-based silent token refresh via /api/auth/refresh.
   *
   * Returns the new access token on success, or null on failure.
   * On failure, AuthContext sets state to "unauthenticated" as a side-effect.
   *
   * IMPORTANT: This is intentionally a raw fetch call (not routed through the
   * typed API client) to avoid a potential infinite refresh-on-refresh loop.
   *
   * Exposed as a stable useCallback reference so useApiClient can pass it to
   * createApiClient without causing unnecessary client rebuilds.
   */
  refreshAccessToken: () => Promise<string | null>;
}

export const AuthContext = createContext<AuthContextValue | null>(null);

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error("useAuth() must be used inside <AuthProvider>");
  return ctx;
}
