import { type FormEvent } from "react";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { useTranslation } from "react-i18next";
import { useAuth } from "./auth-context";

const schema = z.object({
  token: z
    .string()
    .regex(/^[A-Za-z0-9._-]{50,200}$/, "botCredentials.invalidFormat")
    .refine(
      (s) => (s.match(/\./g) ?? []).length === 2,
      "botCredentials.invalidFormat",
    )
    .refine(
      (s) => [...s].filter((c) => /[A-Za-z0-9]/.test(c)).length >= 30,
      "botCredentials.invalidFormat",
    ),
});

type FormValues = z.infer<typeof schema>;

interface Props {
  slug: string;
  guildPublicId: string;
  guildDisplayName: string;
  mode: "configure" | "rotate";
  onClose: () => void;
  onSuccess: () => void;
}

export function BotTokenModal({
  slug,
  guildPublicId,
  guildDisplayName,
  mode,
  onClose,
  onSuccess,
}: Props): React.JSX.Element {
  const { t } = useTranslation();
  const { state } = useAuth();
  const accessToken = state.kind === "authenticated" ? state.accessToken : null;
  const {
    register,
    handleSubmit,
    reset,
    setError,
    formState: { errors, isSubmitting },
  } = useForm<FormValues>({ resolver: zodResolver(schema) });

  async function onSubmit(data: FormValues, e?: React.BaseSyntheticEvent) {
    e?.preventDefault();
    try {
      const putHeaders: Record<string, string> = {
        "Content-Type": "application/json",
      };
      if (accessToken) putHeaders["Authorization"] = `Bearer ${accessToken}`;
      const res = await fetch(
        `/api/t/${encodeURIComponent(slug)}/guilds/${encodeURIComponent(
          guildPublicId,
        )}/bot-credentials`,
        {
          method: "PUT",
          credentials: "include",
          headers: putHeaders,
          body: JSON.stringify({ token: data.token }),
        },
      );
      if (res.status === 204) {
        reset();
        onSuccess();
        onClose();
        return;
      }
      const msg =
        res.status === 403
          ? t("botCredentials.ownerRoleRequired")
          : res.status === 400
            ? t("botCredentials.invalidFormat")
            : t("botCredentials.networkError");
      setError("root", { type: "manual", message: msg });
    } catch {
      setError("root", {
        type: "manual",
        message: t("botCredentials.networkError"),
      });
    }
  }

  return (
    <div
      role="dialog"
      aria-modal="true"
      className="fixed inset-0 bg-black/40 flex items-center justify-center p-4"
    >
      <div className="bg-white rounded p-6 max-w-md w-full">
        <h3 className="text-lg font-semibold">
          {mode === "rotate"
            ? t("botCredentials.modalTitleRotate")
            : t("botCredentials.modalTitleConfigure")}
        </h3>
        <p className="text-sm text-gray-600 mt-1">{guildDisplayName}</p>
        <form
          onSubmit={(e: FormEvent<HTMLFormElement>) =>
            void handleSubmit(onSubmit)(e)
          }
          className="mt-4 space-y-3"
          noValidate
        >
          <div>
            <label className="block text-sm font-medium">
              {t("botCredentials.tokenLabel")}
              <input
                {...register("token")}
                type="password"
                autoComplete="off"
                spellCheck={false}
                data-testid="input-bot-token"
                className="mt-1 block w-full border rounded px-2 py-1 font-mono"
              />
            </label>
            <p className="text-xs text-gray-500 mt-1">
              {t("botCredentials.tokenHint")}
            </p>
            {errors.token && (
              <p data-testid="error-bot-token" className="text-sm text-red-600">
                {t(errors.token.message ?? "")}
              </p>
            )}
          </div>
          {errors.root && (
            <p
              data-testid="error-bot-token-submit"
              className="text-sm text-red-600"
            >
              {errors.root.message}
            </p>
          )}
          <div className="flex gap-3 justify-end">
            <button
              type="button"
              onClick={onClose}
              data-testid="bot-token-cancel"
              className="px-3 py-1 border rounded"
            >
              {t("botCredentials.cancelButton")}
            </button>
            <button
              type="submit"
              disabled={isSubmitting}
              data-testid="bot-token-save"
              className="px-3 py-1 bg-blue-600 text-white rounded disabled:opacity-50"
            >
              {isSubmitting
                ? t("botCredentials.savingButton")
                : t("botCredentials.saveButton")}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}
