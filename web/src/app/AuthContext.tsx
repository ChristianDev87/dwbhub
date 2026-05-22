import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useState,
  type ReactNode,
} from "react";

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

interface AuthContextValue {
  state: AuthState;
  login: (
    slug: string,
    email: string,
    password: string,
  ) => Promise<LoginResult>;
  logout: () => Promise<void>;
  refresh: () => Promise<boolean>;
}

const Ctx = createContext<AuthContextValue | null>(null);

export function useAuth(): AuthContextValue {
  const ctx = useContext(Ctx);
  if (!ctx) throw new Error("useAuth() must be used inside <AuthProvider>");
  return ctx;
}

interface LoginResponse {
  accessToken: string;
  user: User;
  tenant: Tenant;
}

export function AuthProvider({
  children,
}: {
  children: ReactNode;
}): React.JSX.Element {
  const [state, setState] = useState<AuthState>({ kind: "checking" });

  const refresh = useCallback(async (): Promise<boolean> => {
    try {
      const res = await fetch("/api/auth/refresh", {
        method: "POST",
        credentials: "include",
      });
      if (!res.ok) {
        setState({ kind: "unauthenticated" });
        return false;
      }
      // Refresh endpoint returns a new access token; for now we just mark
      // unauthenticated and let the user log in fresh. A richer silent-refresh
      // flow (with cached slug) comes in Plan 0.6+.
      setState({ kind: "unauthenticated" });
      return false;
    } catch {
      setState({ kind: "unauthenticated" });
      return false;
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
    try {
      await fetch("/api/auth/logout", {
        method: "POST",
        credentials: "include",
      });
    } catch {
      // swallow — state cleared either way
    }
    setState({ kind: "unauthenticated" });
  }, []);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  return (
    <Ctx.Provider value={{ state, login, logout, refresh }}>
      {children}
    </Ctx.Provider>
  );
}
