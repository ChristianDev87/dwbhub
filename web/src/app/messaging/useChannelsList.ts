/**
 * useChannelsList — fetches the channel list for a guild and exposes bridge
 * toggle + re-sync operations.
 *
 * Live updates: subscribes to `ChannelBridgeChanged` events via the shared
 * SignalR hub and patches the local list without a full re-fetch.
 *
 * Optimistic UI: bridge toggle immediately updates local state and reverts
 * on API error (4xx / 5xx). The caller is notified via `toggleError`.
 */

import { useCallback, useEffect, useState } from "react";
import { useAuth } from "../auth-context";
import { useMessagesHub } from "./useMessagesHub";
import type { MessageEvent } from "./messages-events";

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

interface ChannelListResponse {
  channels: ChannelListItem[];
}

export interface UseChannelsListResult {
  channels: ChannelListItem[];
  isLoading: boolean;
  error: boolean;
  toggleError: string | null;
  refresh: () => void;
  toggleBridge: (channelPublicId: string, enable: boolean) => Promise<void>;
}

export function useChannelsList(
  slug: string,
  guildPublicId: string,
): UseChannelsListResult {
  const { state } = useAuth();
  const accessToken = state.kind === "authenticated" ? state.accessToken : null;

  const [channels, setChannels] = useState<ChannelListItem[]>([]);
  const [isLoading, setIsLoading] = useState(true);
  const [error, setError] = useState(false);
  const [toggleError, setToggleError] = useState<string | null>(null);

  async function loadChannels() {
    if (!accessToken) return;
    setIsLoading(true);
    setError(false);
    try {
      const res = await fetch(
        `/api/t/${encodeURIComponent(slug)}/guilds/${encodeURIComponent(guildPublicId)}/channels`,
        {
          credentials: "include",
          headers: { Authorization: `Bearer ${accessToken}` },
        },
      );
      if (!res.ok) {
        setError(true);
        return;
      }
      const body = (await res.json()) as ChannelListResponse;
      setChannels(body.channels);
    } catch {
      setError(true);
    } finally {
      setIsLoading(false);
    }
  }

  useEffect(() => {
    if (accessToken) {
      void loadChannels();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [slug, guildPublicId, accessToken]);

  const hubHandler = useCallback(
    (evt: MessageEvent) => {
      if (evt.kind === "ChannelBridgeChanged") {
        const { channelPublicId, isBridged } = evt.payload;
        setChannels((prev) =>
          prev.map((c) =>
            c.publicId === channelPublicId ? { ...c, isBridged } : c,
          ),
        );
      } else if (evt.kind === "BackfillProgress") {
        const { channelPublicId, fetchedCount } = evt.payload;
        setChannels((prev) =>
          prev.map((c) =>
            c.publicId === channelPublicId
              ? {
                  ...c,
                  backfill: {
                    status: "running" as BackfillInfo["status"],
                    fetchedCount,
                  },
                }
              : c,
          ),
        );
      } else if (evt.kind === "BackfillComplete") {
        const { channelPublicId, totalFetched } = evt.payload;
        setChannels((prev) =>
          prev.map((c) =>
            c.publicId === channelPublicId
              ? {
                  ...c,
                  backfill: { status: "complete", fetchedCount: totalFetched },
                }
              : c,
          ),
        );
      }
    },
    // stable: does not depend on any variable from closure
    [],
  );

  useMessagesHub(hubHandler);

  async function toggleBridge(
    channelPublicId: string,
    enable: boolean,
  ): Promise<void> {
    if (!accessToken) return;
    setToggleError(null);

    // Optimistic update
    setChannels((prev) =>
      prev.map((c) =>
        c.publicId === channelPublicId ? { ...c, isBridged: enable } : c,
      ),
    );

    try {
      const url = `/api/t/${encodeURIComponent(slug)}/channels/${encodeURIComponent(channelPublicId)}/bridge`;
      const res = await fetch(url, {
        method: enable ? "POST" : "DELETE",
        credentials: "include",
        headers: { Authorization: `Bearer ${accessToken}` },
      });

      if (!res.ok) {
        // Revert on error
        setChannels((prev) =>
          prev.map((c) =>
            c.publicId === channelPublicId ? { ...c, isBridged: !enable } : c,
          ),
        );
        setToggleError(`HTTP ${res.status.toString()}`);
      }
    } catch {
      // Revert on network error
      setChannels((prev) =>
        prev.map((c) =>
          c.publicId === channelPublicId ? { ...c, isBridged: !enable } : c,
        ),
      );
      setToggleError("network");
    }
  }

  function refresh() {
    // Also triggers a channel sync on the server side
    if (!accessToken) return;
    void (async () => {
      try {
        await fetch(
          `/api/t/${encodeURIComponent(slug)}/guilds/${encodeURIComponent(guildPublicId)}/channels/sync`,
          {
            method: "POST",
            credentials: "include",
            headers: { Authorization: `Bearer ${accessToken}` },
          },
        );
      } catch {
        // Ignore sync errors — loadChannels will still refresh the list
      }
      await loadChannels();
    })();
  }

  return {
    channels,
    isLoading,
    error,
    toggleError,
    refresh,
    toggleBridge,
  };
}
