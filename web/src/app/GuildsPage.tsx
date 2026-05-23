import { useEffect, useState, type FormEvent } from "react";
import { useParams } from "react-router-dom";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { useTranslation } from "react-i18next";
import { BotTokenModal } from "./BotTokenModal";

const discordIdRegex = /^\d{17,20}$/;
const schema = z.object({
  discordGuildId: z
    .string()
    .regex(discordIdRegex, "guilds.discordGuildIdInvalid"),
  displayName: z
    .string()
    .min(1, "guilds.displayNameInvalid")
    .max(100, "guilds.displayNameInvalid"),
});
type FormValues = z.infer<typeof schema>;

interface Guild {
  publicId: string;
  discordGuildId: string;
  displayName: string;
  isActive: boolean;
  registeredAt: string;
  botCredentialsConfigured: boolean;
}

export function GuildsPage(): React.JSX.Element {
  const { t, i18n } = useTranslation();
  const { slug } = useParams<{ slug: string }>();
  const [guilds, setGuilds] = useState<Guild[]>([]);
  const [loadState, setLoadState] = useState<"loading" | "ready" | "error">(
    "loading",
  );
  const [addError, setAddError] = useState<string | null>(null);
  const [pendingDelete, setPendingDelete] = useState<Guild | null>(null);
  const [pendingBotConfigure, setPendingBotConfigure] = useState<{
    guild: Guild;
    mode: "configure" | "rotate";
  } | null>(null);
  const [pendingBotRemove, setPendingBotRemove] = useState<Guild | null>(null);

  const {
    register,
    handleSubmit,
    reset,
    formState: { errors, isSubmitting },
  } = useForm<FormValues>({ resolver: zodResolver(schema) });

  async function loadList() {
    try {
      const res = await fetch(
        `/api/t/${encodeURIComponent(slug ?? "")}/guilds`,
        {
          credentials: "include",
        },
      );
      if (!res.ok) {
        setLoadState("error");
        return;
      }
      const body = (await res.json()) as { guilds: Guild[] };
      setGuilds(body.guilds);
      setLoadState("ready");
    } catch {
      setLoadState("error");
    }
  }

  useEffect(() => {
    void loadList();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [slug]);

  async function onAdd(data: FormValues, e?: React.BaseSyntheticEvent) {
    e?.preventDefault();
    setAddError(null);
    try {
      const res = await fetch(
        `/api/t/${encodeURIComponent(slug ?? "")}/guilds`,
        {
          method: "POST",
          credentials: "include",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(data),
        },
      );
      if (res.status === 409) {
        setAddError(t("guilds.guildAlreadyRegistered"));
        return;
      }
      if (!res.ok) {
        setAddError(t("guilds.networkError"));
        return;
      }
      reset();
      await loadList();
    } catch {
      setAddError(t("guilds.networkError"));
    }
  }

  async function confirmDelete() {
    if (!pendingDelete) return;
    try {
      const res = await fetch(
        `/api/t/${encodeURIComponent(slug ?? "")}/guilds/${pendingDelete.publicId}`,
        { method: "DELETE", credentials: "include" },
      );
      if (res.status === 204) {
        setPendingDelete(null);
        await loadList();
      }
    } catch {
      setPendingDelete(null);
    }
  }

  async function confirmBotRemove() {
    if (!pendingBotRemove) return;
    try {
      const res = await fetch(
        `/api/t/${encodeURIComponent(slug ?? "")}/guilds/${pendingBotRemove.publicId}/bot-credentials`,
        { method: "DELETE", credentials: "include" },
      );
      if (res.status === 204) {
        setPendingBotRemove(null);
        await loadList();
      }
    } catch {
      setPendingBotRemove(null);
    }
  }

  return (
    <div className="max-w-3xl mx-auto p-8">
      <h1 className="text-2xl font-semibold">{t("guilds.title")}</h1>

      <section className="mt-6 border rounded p-4">
        <h2 className="text-lg font-medium">{t("guilds.addSectionTitle")}</h2>
        <form
          onSubmit={(e: FormEvent<HTMLFormElement>) =>
            void handleSubmit(onAdd)(e)
          }
          className="mt-3 space-y-3"
          noValidate
        >
          <div>
            <label className="block text-sm font-medium">
              {t("guilds.discordGuildIdLabel")}
              <input
                {...register("discordGuildId")}
                data-testid="input-discord-guild-id"
                className="mt-1 block w-full border rounded px-2 py-1"
              />
            </label>
            <p className="text-xs text-gray-500">
              {t("guilds.discordGuildIdHint")}
            </p>
            {errors.discordGuildId && (
              <p
                data-testid="error-discord-guild-id"
                className="text-sm text-red-600"
              >
                {t(errors.discordGuildId.message ?? "")}
              </p>
            )}
          </div>
          <div>
            <label className="block text-sm font-medium">
              {t("guilds.displayNameLabel")}
              <input
                {...register("displayName")}
                data-testid="input-guild-display-name"
                className="mt-1 block w-full border rounded px-2 py-1"
              />
            </label>
            {errors.displayName && (
              <p
                data-testid="error-guild-display-name"
                className="text-sm text-red-600"
              >
                {t(errors.displayName.message ?? "")}
              </p>
            )}
          </div>
          {addError && (
            <p data-testid="error-add-guild" className="text-sm text-red-600">
              {addError}
            </p>
          )}
          <button
            type="submit"
            data-testid="add-guild-submit"
            disabled={isSubmitting}
            className="px-4 py-2 bg-blue-600 text-white rounded disabled:opacity-50"
          >
            {isSubmitting ? t("guilds.addingButton") : t("guilds.addButton")}
          </button>
        </form>
      </section>

      <section className="mt-6 border rounded p-4">
        <h2 className="text-lg font-medium">
          {t("guilds.registeredSectionTitle")}
        </h2>
        {loadState === "loading" && <p className="text-gray-500">…</p>}
        {loadState === "error" && (
          <p data-testid="load-error" className="text-red-600">
            {t("guilds.networkError")}
          </p>
        )}
        {loadState === "ready" && guilds.length === 0 && (
          <p data-testid="guilds-empty" className="text-gray-500 mt-2">
            {t("guilds.noGuilds")}
          </p>
        )}
        {loadState === "ready" && guilds.length > 0 && (
          <ul className="mt-2 divide-y">
            {guilds.map((g) => (
              <li
                key={g.publicId}
                className="py-3 flex items-center justify-between"
              >
                <div>
                  <p className="font-medium">{g.displayName}</p>
                  <p className="text-sm text-gray-600">{g.discordGuildId}</p>
                  <p className="text-xs text-gray-500">
                    {t("guilds.registeredAt", {
                      date: new Date(g.registeredAt).toLocaleDateString(
                        i18n.language,
                      ),
                    })}
                  </p>
                </div>
                <div className="flex flex-col items-end gap-2">
                  {g.botCredentialsConfigured ? (
                    <span
                      data-testid="bot-credentials-configured"
                      className="text-green-600 text-xs"
                    >
                      ✓ {t("botCredentials.configured")}
                    </span>
                  ) : (
                    <span
                      data-testid="bot-credentials-missing"
                      className="text-amber-600 text-xs"
                    >
                      ⚠ {t("botCredentials.notConfigured")}
                    </span>
                  )}
                  <div className="flex gap-2">
                    <button
                      type="button"
                      data-testid="configure-bot-token-button"
                      onClick={() =>
                        setPendingBotConfigure({
                          guild: g,
                          mode: g.botCredentialsConfigured
                            ? "rotate"
                            : "configure",
                        })
                      }
                      className="px-3 py-1 bg-blue-600 text-white rounded text-sm"
                    >
                      {g.botCredentialsConfigured
                        ? t("botCredentials.rotateButton")
                        : t("botCredentials.configureButton")}
                    </button>
                    {g.botCredentialsConfigured && (
                      <button
                        type="button"
                        data-testid="remove-bot-token-button"
                        onClick={() => setPendingBotRemove(g)}
                        className="px-3 py-1 border border-red-600 text-red-600 rounded text-sm"
                      >
                        {t("botCredentials.removeButton")}
                      </button>
                    )}
                    <button
                      type="button"
                      data-testid="delete-guild-button"
                      onClick={() => setPendingDelete(g)}
                      className="px-3 py-1 bg-red-600 text-white rounded text-sm"
                    >
                      {t("guilds.deleteButton")}
                    </button>
                  </div>
                </div>
              </li>
            ))}
          </ul>
        )}
      </section>

      {pendingBotConfigure && (
        <BotTokenModal
          slug={slug ?? ""}
          guildPublicId={pendingBotConfigure.guild.publicId}
          guildDisplayName={pendingBotConfigure.guild.displayName}
          mode={pendingBotConfigure.mode}
          onClose={() => setPendingBotConfigure(null)}
          onSuccess={() => void loadList()}
        />
      )}

      {pendingBotRemove && (
        <div
          role="dialog"
          aria-modal="true"
          className="fixed inset-0 bg-black/40 flex items-center justify-center p-4"
        >
          <div className="bg-white rounded p-6 max-w-sm">
            <h3 className="text-lg font-semibold">
              {t("botCredentials.removeConfirmTitle")}
            </h3>
            <p className="mt-2 text-sm">
              {t("botCredentials.removeConfirmText", {
                name: pendingBotRemove.displayName,
              })}
            </p>
            <div className="mt-4 flex gap-3 justify-end">
              <button
                type="button"
                data-testid="bot-remove-confirm-cancel"
                onClick={() => setPendingBotRemove(null)}
                className="px-3 py-1 border rounded"
              >
                {t("botCredentials.removeConfirmCancel")}
              </button>
              <button
                type="button"
                data-testid="bot-remove-confirm-yes"
                onClick={() => void confirmBotRemove()}
                className="px-3 py-1 bg-red-600 text-white rounded"
              >
                {t("botCredentials.removeConfirmYes")}
              </button>
            </div>
          </div>
        </div>
      )}

      {pendingDelete && (
        <div
          role="dialog"
          aria-modal="true"
          className="fixed inset-0 bg-black/40 flex items-center justify-center p-4"
        >
          <div className="bg-white rounded p-6 max-w-sm">
            <h3 className="text-lg font-semibold">
              {t("guilds.deleteConfirmTitle")}
            </h3>
            <p className="mt-2 text-sm">
              {t("guilds.deleteConfirmText", {
                name: pendingDelete.displayName,
              })}
            </p>
            <div className="mt-4 flex gap-3 justify-end">
              <button
                type="button"
                data-testid="delete-confirm-cancel"
                onClick={() => setPendingDelete(null)}
                className="px-3 py-1 border rounded"
              >
                {t("guilds.deleteConfirmCancel")}
              </button>
              <button
                type="button"
                data-testid="delete-confirm-yes"
                onClick={() => void confirmDelete()}
                className="px-3 py-1 bg-red-600 text-white rounded"
              >
                {t("guilds.deleteConfirmYes")}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
