import { useState, type FormEvent } from "react";
import { useParams, Link } from "react-router-dom";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { useTranslation } from "react-i18next";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { useApiClient } from "@/lib/api/useApiClient";
import { qk } from "@/lib/api/queryKeys";
import { BotTokenModal } from "./BotTokenModal";
import { PauseGuildModal } from "./components/PauseGuildModal";

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

type BotConnectionState =
  | "disconnected"
  | "connecting"
  | "connected"
  | "token_invalid"
  | "failed"
  | null;

interface Guild {
  publicId: string;
  discordGuildId: string;
  displayName: string;
  isActive: boolean;
  registeredAt: string;
  botCredentialsConfigured: boolean;
  botConnectionState: BotConnectionState;
}

function statusColor(state: BotConnectionState): string {
  switch (state) {
    case "connected":
      return "text-green-600";
    case "connecting":
      return "text-amber-500";
    case "token_invalid":
    case "failed":
      return "text-red-600";
    case "disconnected":
      return "text-gray-400";
    default:
      return "text-gray-300";
  }
}

export function GuildsPage(): React.JSX.Element {
  const { t, i18n } = useTranslation();
  const { slug } = useParams<{ slug: string }>();
  const api = useApiClient();
  const queryClient = useQueryClient();

  const [addError, setAddError] = useState<string | null>(null);
  const [pendingDelete, setPendingDelete] = useState<Guild | null>(null);
  const [pendingBotConfigure, setPendingBotConfigure] = useState<{
    guild: Guild;
    mode: "configure" | "rotate";
  } | null>(null);
  const [pendingBotRemove, setPendingBotRemove] = useState<Guild | null>(null);
  const [pauseTarget, setPauseTarget] = useState<Guild | null>(null);
  const [actionPending, setActionPending] = useState<string | null>(null);

  const {
    register,
    handleSubmit,
    reset,
    formState: { errors, isSubmitting },
  } = useForm<FormValues>({ resolver: zodResolver(schema) });

  function statusLabel(state: BotConnectionState): string {
    switch (state) {
      case "connected":
        return t("botConnection.status.connected");
      case "connecting":
        return t("botConnection.status.connecting");
      case "disconnected":
        return t("botConnection.status.disconnected");
      case "token_invalid":
        return t("botConnection.status.tokenInvalid");
      case "failed":
        return t("botConnection.status.failed");
      default:
        return t("botConnection.status.unknown");
    }
  }

  const safeSlug = slug ?? "";

  // ── Guild list query with dynamic polling ────────────────────────────────
  const {
    data: guildsData,
    isLoading,
    isError,
  } = useQuery({
    queryKey: qk.guilds.list(safeSlug),
    queryFn: async () => {
      const { data, error } = await api.GET("/api/t/{slug}/guilds", {
        params: { path: { slug: safeSlug } },
      });
      if (error) throw error;
      // The OpenAPI spec returns content: never for this endpoint (spec gap);
      // cast to the known runtime shape.
      return data as unknown as { guilds: Guild[] };
    },
    staleTime: 30_000,
    refetchInterval: (query) => {
      const guilds = (query.state.data as { guilds: Guild[] } | undefined)
        ?.guilds;
      if (!guilds) return false;
      return guilds.some((g) => g.botConnectionState === "connecting")
        ? 3000
        : false;
    },
    enabled: safeSlug.length > 0,
  });

  const guilds: Guild[] = guildsData?.guilds ?? [];
  const loadState: "loading" | "ready" | "error" = isLoading
    ? "loading"
    : isError
      ? "error"
      : "ready";

  // ── Shared invalidation helper ───────────────────────────────────────────
  function invalidateList() {
    return queryClient.invalidateQueries({
      queryKey: qk.guilds.list(safeSlug),
    });
  }

  // ── Add guild mutation ───────────────────────────────────────────────────
  const addMutation = useMutation({
    mutationFn: async (values: FormValues) => {
      const { error, response } = await api.POST("/api/t/{slug}/guilds", {
        params: { path: { slug: safeSlug } },
        body: {
          discordGuildId: values.discordGuildId,
          displayName: values.displayName,
        },
      });
      if (response.status === 409) {
        throw new Error("guild_already_registered");
      }
      if (error) throw error;
    },
    onSuccess: async () => {
      reset();
      await invalidateList();
    },
    onError: (err: Error) => {
      if (err.message === "guild_already_registered") {
        setAddError(t("guilds.guildAlreadyRegistered"));
      } else {
        setAddError(t("guilds.networkError"));
      }
    },
  });

  async function onAdd(data: FormValues, e?: React.BaseSyntheticEvent) {
    e?.preventDefault();
    setAddError(null);
    addMutation.mutate(data);
  }

  // ── Delete guild mutation ────────────────────────────────────────────────
  const deleteMutation = useMutation({
    mutationFn: async (publicId: string) => {
      const { error } = await api.DELETE("/api/t/{slug}/guilds/{publicId}", {
        params: { path: { slug: safeSlug, publicId } },
      });
      if (error) throw error;
    },
    onSuccess: async () => {
      setPendingDelete(null);
      await invalidateList();
    },
    onError: () => {
      setPendingDelete(null);
    },
  });

  async function confirmDelete() {
    if (!pendingDelete) return;
    deleteMutation.mutate(pendingDelete.publicId);
  }

  // ── Delete bot credentials mutation ─────────────────────────────────────
  const deleteBotCredsMutation = useMutation({
    mutationFn: async (guildPublicId: string) => {
      const { error } = await api.DELETE(
        "/api/t/{slug}/guilds/{guildPublicId}/bot-credentials",
        {
          params: { path: { slug: safeSlug, guildPublicId } },
        },
      );
      if (error) throw error;
    },
    onSuccess: async () => {
      setPendingBotRemove(null);
      await invalidateList();
    },
    onError: () => {
      setPendingBotRemove(null);
    },
  });

  async function confirmBotRemove() {
    if (!pendingBotRemove) return;
    deleteBotCredsMutation.mutate(pendingBotRemove.publicId);
  }

  // ── Pause (deactivate) mutation ──────────────────────────────────────────
  const deactivateMutation = useMutation({
    mutationFn: async (publicId: string) => {
      const { error } = await api.POST(
        "/api/t/{slug}/guilds/{publicId}/deactivate",
        {
          params: { path: { slug: safeSlug, publicId } },
        },
      );
      if (error) throw error;
    },
    onSettled: async () => {
      setActionPending(null);
      setPauseTarget(null);
      await invalidateList();
    },
  });

  async function handlePause(g: Guild) {
    setPauseTarget(g);
  }

  async function confirmPause() {
    if (!pauseTarget) return;
    setActionPending(pauseTarget.publicId);
    deactivateMutation.mutate(pauseTarget.publicId);
  }

  // ── Resume (activate) mutation ───────────────────────────────────────────
  const activateMutation = useMutation({
    mutationFn: async (publicId: string) => {
      const { error } = await api.POST(
        "/api/t/{slug}/guilds/{publicId}/activate",
        {
          params: { path: { slug: safeSlug, publicId } },
        },
      );
      if (error) throw error;
    },
    onSettled: async () => {
      await invalidateList();
    },
  });

  async function handleResume(g: Guild) {
    activateMutation.mutate(g.publicId);
  }

  // ── Reconnect mutation ───────────────────────────────────────────────────
  const reconnectMutation = useMutation({
    mutationFn: async (publicId: string) => {
      const { error } = await api.POST(
        "/api/t/{slug}/guilds/{publicId}/bot/reconnect",
        {
          params: { path: { slug: safeSlug, publicId } },
        },
      );
      if (error) throw error;
    },
    onSettled: async () => {
      await invalidateList();
    },
  });

  async function handleReconnect(g: Guild) {
    reconnectMutation.mutate(g.publicId);
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
                  <p
                    data-testid={`guild-status-${g.publicId}`}
                    data-state={g.botConnectionState ?? "null"}
                    className={`text-xs mt-1 ${statusColor(g.botConnectionState)}`}
                  >
                    ● {statusLabel(g.botConnectionState)}
                  </p>
                </div>
                <div className="flex flex-col items-end gap-2">
                  <Link
                    to={`/t/${slug ?? ""}/guilds/${g.publicId}/channels`}
                    data-testid={`guild-channels-link-${g.publicId}`}
                    className="text-sm text-blue-600 hover:underline"
                  >
                    {t("channels.guildsLink")}
                  </Link>
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
                    {g.isActive ? (
                      <>
                        <button
                          type="button"
                          data-testid={`guild-pause-${g.publicId}`}
                          onClick={() => void handlePause(g)}
                          className="px-3 py-1 bg-amber-500 text-white rounded text-sm"
                        >
                          {t("botConnection.actions.pause")}
                        </button>
                        <button
                          type="button"
                          data-testid={`guild-reconnect-${g.publicId}`}
                          disabled={!g.botCredentialsConfigured}
                          onClick={() => void handleReconnect(g)}
                          className="px-3 py-1 bg-blue-500 text-white rounded text-sm disabled:opacity-50"
                        >
                          {t("botConnection.actions.reconnect")}
                        </button>
                      </>
                    ) : (
                      <button
                        type="button"
                        data-testid={`guild-resume-${g.publicId}`}
                        onClick={() => void handleResume(g)}
                        className="px-3 py-1 bg-green-600 text-white rounded text-sm"
                      >
                        {t("botConnection.actions.resume")}
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

      {pauseTarget && (
        <PauseGuildModal
          guildName={pauseTarget.displayName}
          isPending={actionPending === pauseTarget.publicId}
          onCancel={() => setPauseTarget(null)}
          onConfirm={() => void confirmPause()}
        />
      )}

      {pendingBotConfigure && (
        <BotTokenModal
          slug={slug ?? ""}
          guildPublicId={pendingBotConfigure.guild.publicId}
          guildDisplayName={pendingBotConfigure.guild.displayName}
          mode={pendingBotConfigure.mode}
          onClose={() => setPendingBotConfigure(null)}
          onSuccess={() => void invalidateList()}
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
