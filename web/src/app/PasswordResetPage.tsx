import type React from "react";
import { useParams, useSearchParams, useNavigate } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { useMutation } from "@tanstack/react-query";
import { Loader2, AlertCircle } from "lucide-react";
import { useApiClient } from "@/lib/api/useApiClient";

const schema = z.object({
  newPassword: z.string().min(8),
});
type FormValues = z.infer<typeof schema>;

type ErrorReason = "weak_password" | "invalid_or_expired_token" | "network";

export function PasswordResetPage(): React.JSX.Element {
  const { slug } = useParams<{ slug: string }>();
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const { t } = useTranslation();
  const token = params.get("token");
  const api = useApiClient();

  const {
    register,
    handleSubmit,
    formState: { errors },
  } = useForm<FormValues>({ resolver: zodResolver(schema) });

  /**
   * NOTE: The generated schema has `content?: never` for the 200 response of
   * /api/auth/password-reset/confirm (schema gap). openapi-fetch still reads
   * the body internally; on non-2xx responses `error` already contains the
   * parsed JSON body — no need to call `response.json()` again.
   */
  const mutation = useMutation<void, Error, FormValues>({
    mutationFn: async (values: FormValues) => {
      const { error } = await api.POST("/api/auth/password-reset/confirm", {
        body: { token: token ?? "", newPassword: values.newPassword },
      });
      if (error) {
        const body = error as { error?: string } | null;
        const reason: ErrorReason =
          body?.error === "weak_password"
            ? "weak_password"
            : "invalid_or_expired_token";
        throw new Error(reason);
      }
    },
    onSuccess: () => {
      navigate(`/t/${slug}/login`);
    },
  });

  if (!token) {
    return (
      <main className="mx-auto max-w-md p-8" data-testid="reset-missing-token">
        <AlertCircle className="mx-auto h-12 w-12 text-red-600" />
        <p className="mt-2 text-center">{t("passwordReset.missingToken")}</p>
      </main>
    );
  }

  const onSubmit = (values: FormValues) => {
    mutation.mutate(values);
  };

  const serverErrorReason = mutation.isError
    ? (mutation.error?.message as ErrorReason)
    : null;

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
        {serverErrorReason !== null && (
          <p className="text-sm text-red-600" data-testid="reset-server-error">
            {serverErrorReason === "weak_password"
              ? t("passwordReset.weakPassword")
              : serverErrorReason === "invalid_or_expired_token"
                ? t("passwordReset.invalidToken")
                : t("common.error")}
          </p>
        )}
        <button
          type="submit"
          disabled={mutation.isPending}
          data-testid="reset-submit"
          className="inline-flex items-center gap-2 rounded bg-blue-600 px-4 py-2 text-white disabled:opacity-50"
        >
          {mutation.isPending && <Loader2 className="h-4 w-4 animate-spin" />}
          {t("passwordReset.submit")}
        </button>
      </form>
    </main>
  );
}
