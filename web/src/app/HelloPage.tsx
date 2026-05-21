import type React from "react";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";

type HealthResponse = {
  status: string;
  version: string;
  uptime_seconds: number;
};

type HealthState =
  | { kind: "loading" }
  | { kind: "ok"; data: HealthResponse }
  | { kind: "error"; message: string };

export function HelloPage(): React.JSX.Element {
  const { t } = useTranslation();
  const [state, setState] = useState<HealthState>({ kind: "loading" });

  useEffect(() => {
    const controller = new AbortController();
    void (async () => {
      try {
        const res = await fetch("/api/health", { signal: controller.signal });
        if (!res.ok) {
          setState({ kind: "error", message: `HTTP ${res.status}` });
          return;
        }
        const data = (await res.json()) as HealthResponse;
        setState({ kind: "ok", data });
      } catch (err) {
        if ((err as Error).name === "AbortError") return;
        setState({ kind: "error", message: (err as Error).message });
      }
    })();
    return () => controller.abort();
  }, []);

  return (
    <main className="mx-auto flex min-h-screen max-w-2xl flex-col items-center justify-center gap-4 px-6 py-12">
      <h1 className="text-4xl font-bold tracking-tight">{t("hello.title")}</h1>
      <p className="text-lg text-muted-foreground">{t("hello.subtitle")}</p>

      <section
        className="mt-6 w-full rounded-lg border border-border bg-muted/30 p-4"
        aria-live="polite"
        data-testid="api-status"
      >
        <span className="font-semibold">{t("hello.api_status_label")}:</span>{" "}
        {state.kind === "loading" && (
          <span>{t("hello.api_status_loading")}</span>
        )}
        {state.kind === "ok" && (
          <span data-testid="api-status-ok">
            {t("hello.api_status_ok", {
              version: state.data.version,
              uptime: state.data.uptime_seconds,
            })}
          </span>
        )}
        {state.kind === "error" && (
          <span data-testid="api-status-error" className="text-red-600">
            {t("hello.api_status_error")} ({state.message})
          </span>
        )}
      </section>
    </main>
  );
}
