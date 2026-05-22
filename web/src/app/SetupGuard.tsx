import type React from "react";
import { useEffect, useState } from "react";
import { Navigate, useLocation } from "react-router-dom";

type GuardState =
  | { kind: "checking" }
  | { kind: "completed" }
  | { kind: "needs-setup" }
  | { kind: "error" };

/**
 * Top-level wrapper that fetches /api/setup/status once on mount.
 * If setup is incomplete and the user is not already on /setup, declaratively
 * redirects via <Navigate>. Once setup is completed (or the user IS on /setup),
 * renders children. Avoids the "stuck on redirecting state" bug from the
 * previous imperative `navigate()` approach: after the declarative redirect
 * the component re-renders at /setup and falls through to render children.
 */
export function SetupGuard({
  children,
}: {
  children: React.ReactNode;
}): React.JSX.Element {
  const location = useLocation();
  const [state, setState] = useState<GuardState>({ kind: "checking" });

  useEffect(() => {
    const controller = new AbortController();
    void (async () => {
      try {
        const res = await fetch("/api/setup/status", {
          signal: controller.signal,
        });
        if (!res.ok) {
          setState({ kind: "error" });
          return;
        }
        const body = (await res.json()) as { completed: boolean };
        setState({ kind: body.completed ? "completed" : "needs-setup" });
      } catch (err) {
        if ((err as Error).name === "AbortError") return;
        setState({ kind: "error" });
      }
    })();
    return () => controller.abort();
  }, []);

  if (state.kind === "checking") {
    return (
      <div data-testid="setup-guard-checking" className="p-8">
        …
      </div>
    );
  }
  if (state.kind === "error") {
    return (
      <div data-testid="setup-guard-error" className="p-8 text-red-600">
        Cannot reach the API. Check the server.
      </div>
    );
  }
  if (state.kind === "needs-setup" && location.pathname !== "/setup") {
    return <Navigate to="/setup" replace />;
  }
  return <>{children}</>;
}
