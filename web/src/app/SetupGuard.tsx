import type React from "react";
import { useQuery } from "@tanstack/react-query";
import { Navigate, useLocation } from "react-router-dom";
import { useApiClient } from "@/lib/api/useApiClient";
import { qk } from "@/lib/api/queryKeys";

/**
 * Top-level wrapper that fetches /api/setup/status once on mount.
 * If setup is incomplete and the user is not already on /setup, declaratively
 * redirects via <Navigate>. Once setup is completed (or the user IS on /setup),
 * renders children. Avoids the "stuck on redirecting state" bug from the
 * previous imperative `navigate()` approach: after the declarative redirect
 * the component re-renders at /setup and falls through to render children.
 *
 * Uses useApiClient() + TanStack Query instead of a raw fetch/useEffect.
 * The endpoint is public — useApiClient() works with accessToken === null
 * because the auth middleware silently omits the Authorization header.
 */
export function SetupGuard({
  children,
}: {
  children: React.ReactNode;
}): React.JSX.Element {
  const location = useLocation();
  const api = useApiClient();

  const { data, isLoading, isError } = useQuery({
    queryKey: qk.setup.status(),
    queryFn: async () => {
      const { data, error } = await api.GET("/api/setup/status");
      if (error) throw error;
      return data;
    },
  });

  if (isLoading) {
    return (
      <div data-testid="setup-guard-checking" className="p-8">
        …
      </div>
    );
  }
  if (isError || data === undefined) {
    return (
      <div data-testid="setup-guard-error" className="p-8 text-red-600">
        Cannot reach the API. Check the server.
      </div>
    );
  }
  if (!data.completed && location.pathname !== "/setup") {
    return <Navigate to="/setup" replace />;
  }
  return <>{children}</>;
}
