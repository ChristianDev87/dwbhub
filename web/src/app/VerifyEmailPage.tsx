import type React from "react";
import { useEffect, useState } from "react";
import { useParams, useSearchParams, Link } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { Loader2, CheckCircle2, AlertCircle } from "lucide-react";

type VerifyState =
  | { kind: "pending" }
  | { kind: "success" }
  | {
      kind: "error";
      reason: "missing_token" | "invalid_or_expired_token" | "network";
    };

export function VerifyEmailPage(): React.JSX.Element {
  const { slug } = useParams<{ slug: string }>();
  const [params] = useSearchParams();
  const { t } = useTranslation();
  const token = params.get("token");
  const [state, setState] = useState<VerifyState>({ kind: "pending" });

  useEffect(() => {
    if (!token) {
      setState({ kind: "error", reason: "missing_token" });
      return;
    }
    const controller = new AbortController();
    void (async () => {
      try {
        const res = await fetch("/api/auth/verify-email/confirm", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ token }),
          signal: controller.signal,
        });
        if (res.ok) {
          setState({ kind: "success" });
        } else {
          setState({ kind: "error", reason: "invalid_or_expired_token" });
        }
      } catch (err) {
        if ((err as Error).name === "AbortError") return;
        setState({ kind: "error", reason: "network" });
      }
    })();
    return () => controller.abort();
  }, [token]);

  return (
    <main
      className="mx-auto flex min-h-screen max-w-md flex-col items-center justify-center gap-4 px-6 py-12"
      data-testid="verify-email-page"
    >
      {state.kind === "pending" && (
        <div className="flex items-center gap-2" data-testid="verify-pending">
          <Loader2 className="h-5 w-5 animate-spin" />
          <span>{t("verifyEmail.pending")}</span>
        </div>
      )}
      {state.kind === "success" && (
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
      {state.kind === "error" && (
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
