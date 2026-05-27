import { useCallback, useEffect, useState, type ReactNode } from "react";
import { AuthContext, type AuthState, type LoginResult } from "./auth-context";
import { i18n } from "../lib/i18n";
import { queryClient } from "@/lib/api/queryClient";

interface LoginResponse {
  accessToken: string;
  user: {
    id: number;
    email: string;
    displayName: string;
    role: string;
  };
  tenant: {
    id: number;
    slug: string;
    name: string;
    locale?: "de" | "en";
  };
}

interface RefreshResponse {
  accessToken: string;
}

// ---------------------------------------------------------------------------
// sessionStorage helpers — persist auth profile across SPA page navigations.
// The access token is short-lived (24 h); the refresh-token cookie handles
// silent renewal on production HTTPS. In HTTP dev/test environments the
// Secure cookie is not sent, so we fall back to the stored profile so that
// full-page navigations (e.g. Playwright's page.goto) don't lose auth state.
// ---------------------------------------------------------------------------

const PROFILE_KEY = "dwbhub_profile";

interface StoredProfile {
  accessToken: string;
  user: LoginResponse["user"];
  tenant: LoginResponse["tenant"];
}

function loadProfile(): StoredProfile | null {
  try {
    const raw = sessionStorage.getItem(PROFILE_KEY);
    if (!raw) return null;
    return JSON.parse(raw) as StoredProfile;
  } catch {
    return null;
  }
}

function saveProfile(profile: StoredProfile): void {
  try {
    sessionStorage.setItem(PROFILE_KEY, JSON.stringify(profile));
  } catch {
    // storage quota exceeded or unavailable — not critical
  }
}

function clearProfile(): void {
  try {
    sessionStorage.removeItem(PROFILE_KEY);
  } catch {
    // ignore
  }
}

// ---------------------------------------------------------------------------

export function AuthProvider({
  children,
}: {
  children: ReactNode;
}): React.JSX.Element {
  const [state, setState] = useState<AuthState>({ kind: "checking" });

  const refreshAccessToken = useCallback(async (): Promise<string | null> => {
    try {
      const res = await fetch("/api/auth/refresh", {
        method: "POST",
        credentials: "include",
      });
      if (!res.ok) {
        // If there is no stored profile the user is definitively logged out.
        // If there IS a stored profile but the cookie-based refresh failed
        // (e.g. HTTP dev where Secure cookies are not transmitted), keep the
        // current state rather than forcing a logout — the stored access token
        // remains valid until its 24 h expiry.
        if (!loadProfile()) {
          setState({ kind: "unauthenticated" });
        }
        return null;
      }
      const data = (await res.json()) as RefreshResponse;
      const stored = loadProfile();
      if (!stored) {
        // Refresh succeeded but no profile is stored (unexpected).
        setState({ kind: "unauthenticated" });
        return null;
      }
      const updated: StoredProfile = {
        ...stored,
        accessToken: data.accessToken,
      };
      saveProfile(updated);
      setState({
        kind: "authenticated",
        accessToken: updated.accessToken,
        user: updated.user,
        tenant: updated.tenant,
      });
      return updated.accessToken;
    } catch {
      setState({ kind: "unauthenticated" });
      return null;
    }
  }, []);

  const login = useCallback(
    async (
      slug: string,
      email: string,
      password: string,
    ): Promise<LoginResult> => {
      try {
        const res = await fetch(
          `/api/tenants/${encodeURIComponent(slug)}/auth/login`,
          {
            method: "POST",
            credentials: "include",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ email, password }),
          },
        );

        if (res.status === 401) return { kind: "invalid_credentials" };
        if (res.status === 423) {
          const body = (await res.json()) as { retry_after_seconds?: number };
          return {
            kind: "locked_out",
            retryAfterSeconds: body.retry_after_seconds ?? 900,
          };
        }
        if (res.status === 403) {
          const body = (await res.json()) as { error?: string; email?: string };
          if (body.error === "email_not_verified") {
            return { kind: "email_not_verified", email: body.email ?? email };
          }
        }
        if (!res.ok) return { kind: "invalid_credentials" };

        const body = (await res.json()) as LoginResponse;
        const profile: StoredProfile = {
          accessToken: body.accessToken,
          user: body.user,
          tenant: body.tenant,
        };
        saveProfile(profile);
        // Apply the tenant's preferred locale so the UI renders in the
        // correct language immediately after login. Without this call,
        // i18next falls back to the browser's navigator.language, which
        // may differ from the tenant configuration.
        if (body.tenant.locale) {
          void i18n.changeLanguage(body.tenant.locale);
        }
        setState({
          kind: "authenticated",
          accessToken: body.accessToken,
          user: body.user,
          tenant: body.tenant,
        });
        return { kind: "success" };
      } catch {
        return { kind: "network_error" };
      }
    },
    [],
  );

  const logout = useCallback(async (): Promise<void> => {
    // best-effort POST — ignore network errors, state is cleared regardless
    await fetch("/api/auth/logout", {
      method: "POST",
      credentials: "include",
    }).catch(() => {});
    // Evict all cached query data so stale authenticated data is never shown
    // after logout, regardless of which user logs in next.
    queryClient.clear();
    clearProfile();
    setState({ kind: "unauthenticated" });
  }, []);

  useEffect(() => {
    // Restore auth state from sessionStorage immediately so that full-page
    // navigations (React remount) don't flash the login screen for an already-
    // authenticated user. Then attempt a cookie-based token refresh in the
    // background; if it succeeds the access token is silently rotated.
    const stored = loadProfile();
    if (stored) {
      // Restore the tenant locale so full-page navigations (React remount)
      // render the correct language without waiting for the refresh call.
      if (stored.tenant.locale) {
        void i18n.changeLanguage(stored.tenant.locale);
      }
      setState({
        kind: "authenticated",
        accessToken: stored.accessToken,
        user: stored.user,
        tenant: stored.tenant,
      });
    }
    void refreshAccessToken();
  }, [refreshAccessToken]);

  return (
    <AuthContext.Provider value={{ state, login, logout, refreshAccessToken }}>
      {children}
    </AuthContext.Provider>
  );
}
