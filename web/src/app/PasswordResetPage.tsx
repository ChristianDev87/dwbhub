import type React from "react";
import { useState } from "react";
import { useParams, useSearchParams, useNavigate } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { Loader2, AlertCircle } from "lucide-react";

const schema = z.object({
  newPassword: z.string().min(8),
});
type FormValues = z.infer<typeof schema>;

type SubmitState =
  | { kind: "idle" }
  | { kind: "submitting" }
  | {
      kind: "error";
      reason: "weak_password" | "invalid_or_expired_token" | "network";
    };

export function PasswordResetPage(): React.JSX.Element {
  const { slug } = useParams<{ slug: string }>();
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const { t } = useTranslation();
  const token = params.get("token");

  const {
    register,
    handleSubmit,
    formState: { errors },
  } = useForm<FormValues>({ resolver: zodResolver(schema) });

  const [state, setState] = useState<SubmitState>({ kind: "idle" });

  if (!token) {
    return (
      <main className="mx-auto max-w-md p-8" data-testid="reset-missing-token">
        <AlertCircle className="mx-auto h-12 w-12 text-red-600" />
        <p className="mt-2 text-center">{t("passwordReset.missingToken")}</p>
      </main>
    );
  }

  const onSubmit = async (values: FormValues) => {
    setState({ kind: "submitting" });
    try {
      const res = await fetch("/api/auth/password-reset/confirm", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ token, newPassword: values.newPassword }),
      });
      if (res.ok) {
        navigate(`/t/${slug}/login`);
        return;
      }
      const body = (await res.json().catch(() => null)) as {
        error?: string;
      } | null;
      if (body?.error === "weak_password") {
        setState({ kind: "error", reason: "weak_password" });
      } else {
        setState({ kind: "error", reason: "invalid_or_expired_token" });
      }
    } catch {
      setState({ kind: "error", reason: "network" });
    }
  };

  return (
    <main
      className="mx-auto flex min-h-screen max-w-md flex-col justify-center gap-4 px-6 py-12"
      data-testid="password-reset-page"
    >
      <h1 className="text-2xl font-semibold">{t("passwordReset.title")}</h1>
      <form
        onSubmit={handleSubmit(onSubmit)}
        className="space-y-4"
        data-testid="reset-form"
      >
        <label className="block">
          <span>{t("passwordReset.newPasswordLabel")}</span>
          <input
            type="password"
            autoComplete="new-password"
            data-testid="reset-password-input"
            {...register("newPassword")}
            className="mt-1 w-full rounded border p-2"
          />
          {errors.newPassword && (
            <p
              className="mt-1 text-sm text-red-600"
              data-testid="reset-client-error"
            >
              {t("passwordReset.weakPassword")}
            </p>
          )}
        </label>
        {state.kind === "error" && (
          <p className="text-sm text-red-600" data-testid="reset-server-error">
            {state.reason === "weak_password"
              ? t("passwordReset.weakPassword")
              : state.reason === "invalid_or_expired_token"
                ? t("passwordReset.invalidToken")
                : t("common.error")}
          </p>
        )}
        <button
          type="submit"
          disabled={state.kind === "submitting"}
          data-testid="reset-submit"
          className="inline-flex items-center gap-2 rounded bg-blue-600 px-4 py-2 text-white disabled:opacity-50"
        >
          {state.kind === "submitting" && (
            <Loader2 className="h-4 w-4 animate-spin" />
          )}
          {t("passwordReset.submit")}
        </button>
      </form>
    </main>
  );
}
