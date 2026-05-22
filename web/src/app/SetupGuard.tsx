import type React from "react";
import { useEffect, useState } from "react";
import { useLocation, useNavigate } from "react-router-dom";

type GuardState =
  | { kind: "checking" }
  | { kind: "open" }
  | { kind: "redirecting" }
  | { kind: "error" };

/**
 * Top-level wrapper that fetches /api/setup/status once on mount and redirects
 * to /setup if the wizard is not yet complete.
 */
export function SetupGuard({
  children,
}: {
  children: React.ReactNode;
}): React.JSX.Element {
  const navigate = useNavigate();
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
        if (body.completed) {
          setState({ kind: "open" });
        } else if (location.pathname !== "/setup") {
          setState({ kind: "redirecting" });
          navigate("/setup", { replace: true });
        } else {
          setState({ kind: "open" });
        }
      } catch (err) {
        if ((err as Error).name === "AbortError") return;
        setState({ kind: "error" });
      }
    })();
    return () => controller.abort();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  if (state.kind === "checking" || state.kind === "redirecting") {
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
  return <>{children}</>;
}
