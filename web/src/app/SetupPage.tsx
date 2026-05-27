import type React from "react";
import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { useMutation } from "@tanstack/react-query";
import { Loader2, AlertCircle } from "lucide-react";
import { useApiClient } from "@/lib/api/useApiClient";

const slugRegex = /^[a-z0-9](?:[a-z0-9-]{0,30}[a-z0-9])?$/;

const schema = z.object({
  bootstrapToken: z.string().min(1),
  tenantName: z.string().min(1),
  tenantSlug: z.string().regex(slugRegex),
  tenantLocale: z.enum(["de", "en"]),
  ownerEmail: z.string().email(),
  ownerDisplayName: z.string().min(1),
  ownerPassword: z.string().min(8),
});
type FormValues = z.infer<typeof schema>;

/** Shape of the /api/setup/complete response body. */
interface SetupCompleteBody {
  tenantSlug: string;
}

type ErrorReason =
  | "invalid_bootstrap_token"
  | "setup_already_completed"
  | "slug_in_use"
  | "weak_password"
  | "invalid_request"
  | "network";

export function SetupPage(): React.JSX.Element {
  const navigate = useNavigate();
  const { t } = useTranslation();
  const api = useApiClient();
  const {
    register,
    handleSubmit,
    formState: { errors },
  } = useForm<FormValues>({
    resolver: zodResolver(schema),
    defaultValues: { tenantLocale: "de" },
  });
  const [serverError, setServerError] = useState<ErrorReason | null>(null);

  /**
   * NOTE: The generated schema has `content?: never` for the 200/201 response
   * of /api/setup/complete (schema gap). openapi-fetch still parses the JSON
   * body at runtime; we cast `data as unknown` to recover it. On error paths,
   * `error` from openapi-fetch already contains the parsed JSON body.
   */
  const mutation = useMutation({
    mutationFn: async (values: FormValues) => {
      const { data, error } = await api.POST("/api/setup/complete", {
        body: {
          bootstrapToken: values.bootstrapToken,
          tenantName: values.tenantName,
          tenantSlug: values.tenantSlug,
          tenantLocale: values.tenantLocale,
          ownerEmail: values.ownerEmail,
          ownerDisplayName: values.ownerDisplayName,
          ownerPassword: values.ownerPassword,
        },
      });
      if (error) {
        const body = error as { error?: string } | null;
        const reason: ErrorReason =
          body?.error === "invalid_bootstrap_token"
            ? "invalid_bootstrap_token"
            : body?.error === "setup_already_completed"
              ? "setup_already_completed"
              : body?.error === "slug_in_use"
                ? "slug_in_use"
                : body?.error === "weak_password"
                  ? "weak_password"
                  : "invalid_request";
        throw new Error(reason);
      }
      // openapi-fetch parses the JSON body at runtime even when the schema
      // declares `content?: never`; cast to recover the actual value.
      return (data as unknown) as SetupCompleteBody;
    },
    onSuccess: (result) => {
      navigate(`/t/${result.tenantSlug}/verify-email-prompt`);
    },
    onError: (err: Error) => {
      const reason = (err.message as ErrorReason) ?? "network";
      setServerError(reason);
    },
  });

  const onSubmit = (values: FormValues) => {
    setServerError(null);
    mutation.mutate(values);
  };

  const isSubmitting = mutation.isPending;

  return (
    <main
      className="mx-auto flex min-h-screen max-w-md flex-col justify-center gap-4 px-6 py-12"
      data-testid="setup-page"
    >
      <h1 className="text-2xl font-semibold">{t("setup.title")}</h1>
      <p className="text-sm text-gray-600">{t("setup.intro")}</p>
      <form
        onSubmit={handleSubmit(onSubmit)}
        className="space-y-3"
        data-testid="setup-form"
      >
        <label className="block">
          <span>{t("setup.bootstrapTokenLabel")}</span>
          <input
            type="text"
            autoComplete="off"
            data-testid="setup-bootstrap-token"
            {...register("bootstrapToken")}
            className="mt-1 w-full rounded border p-2 font-mono text-sm"
          />
          {errors.bootstrapToken && (
            <p className="text-sm text-red-600">
              {t("setup.bootstrapTokenRequired")}
            </p>
          )}
        </label>
        <label className="block">
          <span>{t("setup.tenantNameLabel")}</span>
          <input
            type="text"
            data-testid="setup-tenant-name"
            {...register("tenantName")}
            className="mt-1 w-full rounded border p-2"
          />
          {errors.tenantName && (
            <p className="text-sm text-red-600">
              {t("setup.tenantNameRequired")}
            </p>
          )}
        </label>
        <label className="block">
          <span>{t("setup.tenantSlugLabel")}</span>
          <input
            type="text"
            data-testid="setup-tenant-slug"
            {...register("tenantSlug")}
            className="mt-1 w-full rounded border p-2 font-mono"
          />
          {errors.tenantSlug && (
            <p className="text-sm text-red-600">
              {t("setup.tenantSlugInvalid")}
            </p>
          )}
        </label>
        <label className="block">
          <span>{t("setup.tenantLocaleLabel")}</span>
          <select
            data-testid="setup-tenant-locale"
            {...register("tenantLocale")}
            className="mt-1 w-full rounded border p-2"
          >
            <option value="de">Deutsch</option>
            <option value="en">English</option>
          </select>
        </label>
        <label className="block">
          <span>{t("setup.ownerEmailLabel")}</span>
          <input
            type="email"
            autoComplete="email"
            data-testid="setup-owner-email"
            {...register("ownerEmail")}
            className="mt-1 w-full rounded border p-2"
          />
          {errors.ownerEmail && (
            <p className="text-sm text-red-600">
              {t("setup.ownerEmailInvalid")}
            </p>
          )}
        </label>
        <label className="block">
          <span>{t("setup.ownerDisplayNameLabel")}</span>
          <input
            type="text"
            data-testid="setup-owner-display-name"
            {...register("ownerDisplayName")}
            className="mt-1 w-full rounded border p-2"
          />
          {errors.ownerDisplayName && (
            <p className="text-sm text-red-600">
              {t("setup.ownerDisplayNameRequired")}
            </p>
          )}
        </label>
        <label className="block">
          <span>{t("setup.ownerPasswordLabel")}</span>
          <input
            type="password"
            autoComplete="new-password"
            data-testid="setup-owner-password"
            {...register("ownerPassword")}
            className="mt-1 w-full rounded border p-2"
          />
          {errors.ownerPassword && (
            <p className="text-sm text-red-600">
              {t("setup.ownerPasswordTooShort")}
            </p>
          )}
        </label>
        {serverError !== null && (
          <p
            className="text-sm text-red-600 flex items-center gap-2"
            data-testid="setup-server-error"
          >
            <AlertCircle className="h-4 w-4" />
            {t(`setup.errors.${serverError}`)}
          </p>
        )}
        <button
          type="submit"
          disabled={isSubmitting}
          data-testid="setup-submit"
          className="inline-flex items-center gap-2 rounded bg-blue-600 px-4 py-2 text-white disabled:opacity-50"
        >
          {isSubmitting && (
            <Loader2 className="h-4 w-4 animate-spin" />
          )}
          {t("setup.submit")}
        </button>
      </form>
    </main>
  );
}
