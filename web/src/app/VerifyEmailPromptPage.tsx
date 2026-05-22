import type React from "react";
import { useParams } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { CheckCircle2 } from "lucide-react";

export function VerifyEmailPromptPage(): React.JSX.Element {
  const { slug } = useParams<{ slug: string }>();
  const { t } = useTranslation();

  return (
    <main
      className="mx-auto flex min-h-screen max-w-md flex-col items-center justify-center gap-4 px-6 py-12 text-center"
      data-testid="verify-email-prompt-page"
    >
      <CheckCircle2 className="h-12 w-12 text-green-600" />
      <h1 className="text-2xl font-semibold">{t("verifyEmailPrompt.title")}</h1>
      <p>{t("verifyEmailPrompt.body")}</p>
      <p className="text-sm text-gray-500 mt-4" data-testid="verify-email-prompt-slug">
        {t("verifyEmailPrompt.tenantHint", { slug })}
      </p>
    </main>
  );
}
