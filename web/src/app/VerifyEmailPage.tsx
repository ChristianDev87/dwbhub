import type React from "react";
import { useEffect } from "react";
import { useParams, useSearchParams, Link } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { useMutation } from "@tanstack/react-query";
import { Loader2, CheckCircle2, AlertCircle } from "lucide-react";
import { useApiClient } from "@/lib/api/useApiClient";

export function VerifyEmailPage(): React.JSX.Element {
  const { slug } = useParams<{ slug: string }>();
  const [params] = useSearchParams();
  const { t } = useTranslation();
  const token = params.get("token");
  const api = useApiClient();

  /**
   * NOTE: The generated schema has `content?: never` for the 200 response of
   * /api/auth/verify-email/confirm (schema gap). openapi-fetch still reads the
   * body internally; on non-2xx responses `error` contains the parsed JSON.
   * We only need success/failure state here, so the body value is ignored.
   */
  const mutation = useMutation<void, Error, string>({
    mutationFn: async (verifyToken: string) => {
      const { error } = await api.POST("/api/auth/verify-email/confirm", {
        body: { token: verifyToken },
      });
      if (error) throw new Error("invalid_or_expired_token");
    },
  });

  useEffect(() => {
    if (!token) return;
    mutation.mutate(token);
    // The effect must run exactly once per token; exhaustive-deps is intentionally
    // omitted for mutation (stable ref) to avoid re-triggering on re-renders.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [token]);

  const isMissingToken = !token;
  const isPending = Boolean(token) && (mutation.isIdle || mutation.isPending);
  const isSuccess = mutation.isSuccess;
  const isError = isMissingToken || mutation.isError;

  return (
    <main
      className="mx-auto flex min-h-screen max-w-md flex-col items-center justify-center gap-4 px-6 py-12"
      data-testid="verify-email-page"
    >
      {isPending && (
        <div className="flex items-center gap-2" data-testid="verify-pending">
          <Loader2 className="h-5 w-5 animate-spin" />
          <span>{t("verifyEmail.pending")}</span>
        </div>
      )}
      {isSuccess && (
        <div className="text-center" data-testid="verify-success">
          <CheckCircle2 className="mx-auto h-12 w-12 text-green-600" />
          <h1 className="mt-4 text-2xl font-semibold">
            {t("verifyEmail.successTitle")}
          </h1>
          <p className="mt-2">{t("verifyEmail.successBody")}</p>
          <Link
            to={`/t/${slug}/login`}
            className="mt-6 inline-block text-blue-600 underline"
            data-testid="verify-go-to-login"
          >
            {t("verifyEmail.goToLogin")}
          </Link>
        </div>
      )}
      {isError && (
        <div className="text-center" data-testid="verify-error">
          <AlertCircle className="mx-auto h-12 w-12 text-red-600" />
          <h1 className="mt-4 text-2xl font-semibold">
            {t("verifyEmail.errorTitle")}
          </h1>
          <p className="mt-2">{t("verifyEmail.errorBody")}</p>
        </div>
      )}
    </main>
  );
}
