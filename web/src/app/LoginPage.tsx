import { useState, type FormEvent } from "react";
import { useNavigate } from "react-router-dom";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { useTranslation } from "react-i18next";
import { useAuth } from "./auth-context";

const slugRegex = /^[a-z0-9](?:[a-z0-9-]{0,30}[a-z0-9])?$/;

const schema = z.object({
  tenantSlug: z.string().regex(slugRegex, "loginpage.invalidSlug"),
  email: z.string().email("loginpage.invalidEmail"),
  password: z.string().min(8, "loginpage.passwordTooShort"),
});
type FormValues = z.infer<typeof schema>;

export function LoginPage(): React.JSX.Element {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { login } = useAuth();
  const [submitError, setSubmitError] = useState<string | null>(null);

  const {
    register,
    handleSubmit,
    formState: { errors, isSubmitting },
  } = useForm<FormValues>({ resolver: zodResolver(schema) });

  const onSubmit = async (data: FormValues, e?: React.BaseSyntheticEvent) => {
    e?.preventDefault();
    setSubmitError(null);
    const result = await login(data.tenantSlug, data.email, data.password);
    switch (result.kind) {
      case "success":
        navigate(`/t/${data.tenantSlug}/dashboard`);
        return;
      case "email_not_verified":
        navigate(`/t/${data.tenantSlug}/verify-email-prompt`);
        return;
      case "locked_out":
        setSubmitError(
          t("loginpage.lockedOut", { seconds: result.retryAfterSeconds }),
        );
        return;
      case "network_error":
        setSubmitError(t("loginpage.networkError"));
        return;
      case "invalid_credentials":
      default:
        setSubmitError(t("loginpage.invalidCredentials"));
        return;
    }
  };

  return (
    <div className="max-w-md mx-auto p-8">
      <h1 className="text-2xl font-semibold">{t("loginpage.title")}</h1>
      <form
        className="mt-6 space-y-4"
        onSubmit={(e: FormEvent<HTMLFormElement>) =>
          void handleSubmit(onSubmit)(e)
        }
        noValidate
      >
        <div>
          <label className="block text-sm font-medium">
            {t("loginpage.tenantSlug")}
            <input
              {...register("tenantSlug")}
              data-testid="input-tenantSlug"
              className="mt-1 block w-full border rounded px-2 py-1"
              autoComplete="organization"
            />
          </label>
          <p className="text-xs text-gray-500 mt-1">
            {t("loginpage.tenantSlugHint")}
          </p>
          {errors.tenantSlug && (
            <p data-testid="error-tenantSlug" className="text-sm text-red-600">
              {t(errors.tenantSlug.message ?? "")}
            </p>
          )}
        </div>
        <div>
          <label className="block text-sm font-medium">
            {t("loginpage.email")}
            <input
              type="email"
              {...register("email")}
              data-testid="input-email"
              className="mt-1 block w-full border rounded px-2 py-1"
              autoComplete="email"
            />
          </label>
          {errors.email && (
            <p data-testid="error-email" className="text-sm text-red-600">
              {t(errors.email.message ?? "")}
            </p>
          )}
        </div>
        <div>
          <label className="block text-sm font-medium">
            {t("loginpage.password")}
            <input
              type="password"
              {...register("password")}
              data-testid="input-password"
              className="mt-1 block w-full border rounded px-2 py-1"
              autoComplete="current-password"
            />
          </label>
          {errors.password && (
            <p data-testid="error-password" className="text-sm text-red-600">
              {t(errors.password.message ?? "")}
            </p>
          )}
        </div>
        {submitError && (
          <p data-testid="error-submit" className="text-sm text-red-600">
            {submitError}
          </p>
        )}
        <button
          type="submit"
          data-testid="login-submit"
          disabled={isSubmitting}
          className="px-4 py-2 bg-blue-600 text-white rounded disabled:opacity-50"
        >
          {isSubmitting ? t("loginpage.submitting") : t("loginpage.submit")}
        </button>
      </form>
    </div>
  );
}
