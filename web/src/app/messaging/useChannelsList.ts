/**
 * useChannelsList — fetches the channel list for a guild and exposes bridge
 * toggle + re-sync operations.
 *
 * Migrated to typed openapi-fetch + TanStack Query (PR 5c).
 *
 * Live updates: subscribes to `ChannelBridgeChanged`, `BackfillProgress` and
 * `BackfillComplete` events via the shared SignalR hub and patches the cache
 * without a full re-fetch.
 *
 * Optimistic UI: bridge toggle immediately updates the cache and reverts on
 * API error (4xx / 5xx). The caller is notified via `toggleError`.
 */

import { useCallback } from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { useApiClient } from "@/lib/api/useApiClient";
import { qk } from "@/lib/api/queryKeys";
import { useMessagesHub } from "./useMessagesHub";
import type { MessageEvent } from "./messages-events";
import type { components } from "@/lib/api/generated/schema";

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

type SchemaChannelListItem = components["schemas"]["ChannelListItem"];
type SchemaBackfillItem = components["schemas"]["BackfillStatusItem"];
type SchemaChannelListResponse = components["schemas"]["ChannelListResponse"];

export interface BackfillInfo {
  status: "pending" | "running" | "complete" | "failed" | "cancelled";
  fetchedCount: number;
}

export interface ChannelListItem {
  publicId: string;
  discordChannelId: number | string;
  name: string;
  channelType: number; // 0 = text; others = voice/stage/etc.
  isBridged: boolean;
  backfill?: BackfillInfo;
}

export interface UseChannelsListResult {
  channels: ChannelListItem[];
  isLoading: boolean;
  error: boolean;
  toggleError: string | null;
  refresh: () => void;
  toggleBridge: (channelPublicId: string, enable: boolean) => Promise<void>;
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function mapSchemaItem(item: SchemaChannelListItem): ChannelListItem {
  const base: ChannelListItem = {
    publicId: item.publicId ?? "",
    discordChannelId: item.discordChannelId ?? 0,
    name: item.name ?? "",
    channelType: item.channelType ?? 0,
    isBridged: item.isBridged ?? false,
  };
  if (item.backfill) {
    const b = item.backfill as SchemaBackfillItem;
    base.backfill = {
      status: (b.status ?? "pending") as BackfillInfo["status"],
      fetchedCount: b.fetchedCount ?? 0,
    };
  }
  return base;
}

// ---------------------------------------------------------------------------
// Hook
// ---------------------------------------------------------------------------

export function useChannelsList(
  slug: string,
  guildPublicId: string,
): UseChannelsListResult {
  const api = useApiClient();
  const queryClient = useQueryClient();

  // ── Channel list query ───────────────────────────────────────────────────
  const { data, isLoading, isError } = useQuery({
    queryKey: qk.channels.list(slug, guildPublicId),
    queryFn: async () => {
      const { data: respData, error } = await api.GET(
        "/api/t/{slug}/guilds/{guildPublicId}/channels",
        {
          params: { path: { slug, guildPublicId } },
        },
      );
      if (error) throw error;
      return respData as SchemaChannelListResponse;
    },
    staleTime: 30_000,
    enabled: slug.length > 0 && guildPublicId.length > 0,
  });

  const channels: ChannelListItem[] = (data?.channels ?? []).map(mapSchemaItem);

  // ── Toggle-error state — stored separately (not in query data) ───────────
  // We use useMutation's error state for display.

  // ── Bridge / Unbridge mutation with optimistic update ───────────────────
  const toggleBridgeMutation = useMutation<
    void,
    { code: string },
    { channelPublicId: string; enable: boolean },
    { previousData: SchemaChannelListResponse | undefined }
  >({
    mutationFn: async ({ channelPublicId, enable }) => {
      if (enable) {
        const { error } = await api.POST(
          "/api/t/{slug}/channels/{channelPublicId}/bridge",
          {
            params: { path: { slug, channelPublicId } },
          },
        );
        if (error) {
          const status = (error as { status?: number }).status ?? "error";
          throw { code: `HTTP ${String(status)}` };
        }
      } else {
        const { error } = await api.DELETE(
          "/api/t/{slug}/channels/{channelPublicId}/bridge",
          {
            params: { path: { slug, channelPublicId } },
          },
        );
        if (error) {
          const status = (error as { status?: number }).status ?? "error";
          throw { code: `HTTP ${String(status)}` };
        }
      }
    },
    onMutate: async ({ channelPublicId, enable }) => {
      await queryClient.cancelQueries({
        queryKey: qk.channels.list(slug, guildPublicId),
      });
      const previousData = queryClient.getQueryData<SchemaChannelListResponse>(
        qk.channels.list(slug, guildPublicId),
      );
      // Optimistic update
      queryClient.setQueryData<SchemaChannelListResponse>(
        qk.channels.list(slug, guildPublicId),
        (old) => {
          if (!old) return old;
          return {
            ...old,
            channels: (old.channels ?? []).map((c) =>
              c.publicId === channelPublicId ? { ...c, isBridged: enable } : c,
            ),
          };
        },
      );
      return { previousData };
    },
    onError: (_err, _vars, ctx) => {
      // Rollback
      if (ctx?.previousData !== undefined) {
        queryClient.setQueryData(
          qk.channels.list(slug, guildPublicId),
          ctx.previousData,
        );
      }
    },
    onSettled: () => {
      void queryClient.invalidateQueries({
        queryKey: qk.channels.list(slug, guildPublicId),
      });
    },
  });

  // Exposed async wrapper so ChannelsPage can await it (matching the old API).
  // Errors are surfaced via `toggleError` (mutation.error); we swallow the
  // thrown rejection here so that `void toggleBridge(...)` callers in the
  // component do not produce an unhandled promise rejection.
  async function toggleBridge(
    channelPublicId: string,
    enable: boolean,
  ): Promise<void> {
    try {
      await toggleBridgeMutation.mutateAsync({ channelPublicId, enable });
    } catch {
      // Intentionally swallowed — error is exposed via toggleError.
    }
  }

  // ── Sync mutation ────────────────────────────────────────────────────────
  const syncMutation = useMutation({
    mutationFn: async () => {
      const { error } = await api.POST(
        "/api/t/{slug}/guilds/{guildPublicId}/channels/sync",
        {
          params: { path: { slug, guildPublicId } },
        },
      );
      if (error) throw error;
    },
    onSettled: () => {
      void queryClient.invalidateQueries({
        queryKey: qk.channels.list(slug, guildPublicId),
      });
    },
  });

  function refresh() {
    syncMutation.mutate();
  }

  // ── SignalR handler — writes into the exact same query cache key ─────────
  const hubHandler = useCallback(
    (evt: MessageEvent) => {
      if (evt.kind === "ChannelBridgeChanged") {
        const { channelPublicId, isBridged } = evt.payload;
        queryClient.setQueryData<SchemaChannelListResponse>(
          qk.channels.list(slug, guildPublicId),
          (old) => {
            if (!old) return old;
            return {
              ...old,
              channels: (old.channels ?? []).map((c) =>
                c.publicId === channelPublicId ? { ...c, isBridged } : c,
              ),
            };
          },
        );
      } else if (evt.kind === "BackfillProgress") {
        const { channelPublicId, fetchedCount } = evt.payload;
        queryClient.setQueryData<SchemaChannelListResponse>(
          qk.channels.list(slug, guildPublicId),
          (old) => {
            if (!old) return old;
            return {
              ...old,
              channels: (old.channels ?? []).map((c) =>
                c.publicId === channelPublicId
                  ? {
                      ...c,
                      backfill: {
                        status: "running",
                        fetchedCount,
                      } as SchemaBackfillItem,
                    }
                  : c,
              ),
            };
          },
        );
      } else if (evt.kind === "BackfillComplete") {
        const { channelPublicId, totalFetched } = evt.payload;
        queryClient.setQueryData<SchemaChannelListResponse>(
          qk.channels.list(slug, guildPublicId),
          (old) => {
            if (!old) return old;
            return {
              ...old,
              channels: (old.channels ?? []).map((c) =>
                c.publicId === channelPublicId
                  ? {
                      ...c,
                      backfill: {
                        status: "complete",
                        fetchedCount: totalFetched,
                      } as SchemaBackfillItem,
                    }
                  : c,
              ),
            };
          },
        );
      }
    },
    [queryClient, slug, guildPublicId],
  );

  useMessagesHub(hubHandler);

  // ── Derive toggle error from mutation state ──────────────────────────────
  const mutErr = toggleBridgeMutation.error as { code?: string } | null;
  const toggleError = mutErr ? (mutErr.code ?? "error") : null;

  return {
    channels,
    isLoading,
    error: isError,
    toggleError,
    refresh,
    toggleBridge,
  };
}
