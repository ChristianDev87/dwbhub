import type React from "react";
import { useTranslation } from "react-i18next";
import { useQuery } from "@tanstack/react-query";
import { useApiClient } from "@/lib/api/useApiClient";
import { qk } from "@/lib/api/queryKeys";

/** Shape of the /api/health response body. */
type HealthResponse = {
  status: string;
  version: string;
  uptime_seconds: number;
};

export function HelloPage(): React.JSX.Element {
  const { t } = useTranslation();
  const api = useApiClient();

  /**
   * NOTE: The generated schema has `content?: never` for the 200 response of
   * /api/health (schema gap). openapi-fetch still parses the JSON body at
   * runtime; we cast `data as unknown` to recover the actual value.
   * On network failures, `error` from openapi-fetch is the thrown Error instance.
   */
  const { data, isError, error } = useQuery({
    queryKey: qk.health(),
    queryFn: async (): Promise<HealthResponse> => {
      const { data: raw, error: fetchError } = await api.GET("/api/health");
      if (fetchError) throw fetchError;
      return (raw as unknown) as HealthResponse;
    },
  });

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
        {data === undefined && !isError && (
          <span>{t("hello.api_status_loading")}</span>
        )}
        {data !== undefined && (
          <span data-testid="api-status-ok">
            {t("hello.api_status_ok", {
              version: data.version,
              uptime: data.uptime_seconds,
            })}
          </span>
        )}
        {isError && (
          <span data-testid="api-status-error" className="text-red-600">
            {t("hello.api_status_error")} ({(error as Error)?.message ?? "unknown"})
          </span>
        )}
      </section>
    </main>
  );
}
