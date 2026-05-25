/**
 * useChannel — chat history + live updates + send for a single channel.
 *
 * Responsibilities:
 *   - Initial fetch of message history (newest-first, then reversed for display)
 *   - Lazy-load of older pages via loadOlder()
 *   - Live SignalR events: MessageReceived, MessageUpdated, MessageDeleted
 *   - Optimistic send with echo deduplication by discordMessageId
 *
 * AbortController is used on all fetches so that stale responses from
 * StrictMode double-mounts are discarded before they can call setState.
 */

import { useCallback, useEffect, useRef, useState } from "react";
import { useAuth } from "../auth-context";
import { useMessagesHub } from "./useMessagesHub";
import type { MessageEvent } from "./messages-events";

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

export interface ChatMessage {
  /** Positive from backend; negative temp ID for pending sends. */
  id: number;
  authorName: string;
  content: string;
  sentAt: string;
  editedAt: string | null;
  viaDwbhub: boolean;
  /** null while the POST is in-flight (pending send). */
  discordMessageId: number | string | null;
  isDeleted: boolean;
  isPending: boolean;
}

// Backend history item shape (from GET /messages)
interface MessageHistoryItem {
  id: number;
  authorName: string;
  content: string;
  sentAt: string;
  editedAt: string | null;
  viaDwbhub: boolean;
}

interface MessageHistoryResponse {
  messages: MessageHistoryItem[];
  nextBefore: number | null;
}

// Backend send-response shape (from POST /messages)
interface SendMessageResponse {
  id: number;
  discordMessageId: number;
  sentAt: string;
}

export interface UseChannelResult {
  messages: ChatMessage[];
  isLoading: boolean;
  error: string | null;
  hasMore: boolean;
  isLoadingOlder: boolean;
  isSending: boolean;
  sendError: string | null;
  newCount: number;
  loadOlder: () => void;
  sendMessage: (content: string) => Promise<void>;
  markAtBottom: (atBottom: boolean) => void;
}

// ---------------------------------------------------------------------------
// Hook
// ---------------------------------------------------------------------------

export function useChannel(
  slug: string,
  channelPublicId: string,
): UseChannelResult {
  const { state } = useAuth();
  const accessToken = state.kind === "authenticated" ? state.accessToken : null;

  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [isLoading, setIsLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [hasMore, setHasMore] = useState(false);
  const [isLoadingOlder, setIsLoadingOlder] = useState(false);
  const [isSending, setIsSending] = useState(false);
  const [sendError, setSendError] = useState<string | null>(null);
  const [newCount, setNewCount] = useState(0);

  // Track whether the user is at the bottom of the list
  const atBottomRef = useRef(true);

  // cursor for pagination (discordMessageId of oldest loaded message)
  const nextBeforeRef = useRef<number | null>(null);

  // AbortController for the initial history fetch
  const initAbortRef = useRef<AbortController | null>(null);
  // AbortController for in-flight "load older" fetch
  const olderAbortRef = useRef<AbortController | null>(null);

  // ---------------------------------------------------------------------------
  // Helpers
  // ---------------------------------------------------------------------------

  function historyItemToChat(item: MessageHistoryItem): ChatMessage {
    return {
      id: item.id,
      authorName: item.authorName,
      content: item.content,
      sentAt: item.sentAt,
      editedAt: item.editedAt ?? null,
      viaDwbhub: item.viaDwbhub,
      discordMessageId: null, // history items have no discordMessageId from this endpoint
      isDeleted: false,
      isPending: false,
    };
  }

  // ---------------------------------------------------------------------------
  // Initial load
  // ---------------------------------------------------------------------------

  useEffect(() => {
    if (!accessToken) return;

    // Cancel any previous in-flight initial fetch (StrictMode double-mount)
    initAbortRef.current?.abort();
    const controller = new AbortController();
    initAbortRef.current = controller;

    setIsLoading(true);
    setError(null);

    void (async () => {
      try {
        const url = `/api/t/${encodeURIComponent(slug)}/channels/${encodeURIComponent(channelPublicId)}/messages?limit=50`;
        const res = await fetch(url, {
          credentials: "include",
          headers: { Authorization: `Bearer ${accessToken}` },
          signal: controller.signal,
        });

        if (!res.ok) {
          setError("load");
          return;
        }

        const body = (await res.json()) as MessageHistoryResponse;

        // API returns newest-first; reverse for display (oldest at top)
        const items: ChatMessage[] = [...body.messages]
          .reverse()
          .map(historyItemToChat);

        setMessages(items);
        setHasMore(body.nextBefore !== null);
        nextBeforeRef.current = body.nextBefore;
      } catch (err: unknown) {
        if ((err as { name?: string }).name === "AbortError") return;
        setError("load");
      } finally {
        if (!controller.signal.aborted) {
          setIsLoading(false);
        }
      }
    })();

    return () => {
      controller.abort();
    };
  }, [slug, channelPublicId, accessToken]);

  // ---------------------------------------------------------------------------
  // Load older messages (scroll-to-top pagination)
  // ---------------------------------------------------------------------------

  const loadOlder = useCallback(() => {
    if (!accessToken || !hasMore || isLoadingOlder) return;
    if (nextBeforeRef.current === null) return;

    // Cancel any previous in-flight older fetch
    olderAbortRef.current?.abort();
    const controller = new AbortController();
    olderAbortRef.current = controller;

    setIsLoadingOlder(true);

    void (async () => {
      try {
        const url = `/api/t/${encodeURIComponent(slug)}/channels/${encodeURIComponent(channelPublicId)}/messages?limit=50&before=${String(nextBeforeRef.current)}`;
        const res = await fetch(url, {
          credentials: "include",
          headers: { Authorization: `Bearer ${accessToken}` },
          signal: controller.signal,
        });

        if (!res.ok) return;

        const body = (await res.json()) as MessageHistoryResponse;
        const older: ChatMessage[] = [...body.messages]
          .reverse()
          .map(historyItemToChat);

        setMessages((prev) => [...older, ...prev]);
        setHasMore(body.nextBefore !== null);
        nextBeforeRef.current = body.nextBefore;
      } catch (err: unknown) {
        if ((err as { name?: string }).name === "AbortError") return;
        // Silently ignore older-page errors; user can scroll again to retry
      } finally {
        if (!controller.signal.aborted) {
          setIsLoadingOlder(false);
        }
      }
    })();
  }, [slug, channelPublicId, accessToken, hasMore, isLoadingOlder]);

  // ---------------------------------------------------------------------------
  // Live SignalR events
  // ---------------------------------------------------------------------------

  const hubHandler = useCallback(
    (evt: MessageEvent) => {
      if (evt.kind === "MessageReceived") {
        const p = evt.payload;
        if (p.channelPublicId !== channelPublicId) return;

        setMessages((prev) => {
          // Check if this echoes a pending send (match by discordMessageId)
          const pendingIdx = prev.findIndex(
            (m) =>
              m.isPending &&
              m.discordMessageId !== null &&
              String(m.discordMessageId) === String(p.discordMessageId),
          );

          if (pendingIdx !== -1) {
            // Replace pending entry with confirmed message
            const updated = [...prev];
            updated[pendingIdx] = {
              id: p.id,
              authorName: p.authorName,
              content: p.content,
              sentAt: p.sentAt,
              editedAt: null,
              viaDwbhub: p.viaDwbhub,
              discordMessageId: p.discordMessageId,
              isDeleted: false,
              isPending: false,
            };
            return updated;
          }

          // New message from someone else (or unmatched — append)
          const newMsg: ChatMessage = {
            id: p.id,
            authorName: p.authorName,
            content: p.content,
            sentAt: p.sentAt,
            editedAt: null,
            viaDwbhub: p.viaDwbhub,
            discordMessageId: p.discordMessageId,
            isDeleted: false,
            isPending: false,
          };

          if (!atBottomRef.current) {
            setNewCount((n) => n + 1);
          }

          return [...prev, newMsg];
        });
      } else if (evt.kind === "MessageUpdated") {
        const p = evt.payload;
        if (p.channelPublicId !== channelPublicId) return;

        setMessages((prev) =>
          prev.map((m) =>
            m.id === p.id
              ? { ...m, content: p.content, editedAt: p.editedAt }
              : m,
          ),
        );
      } else if (evt.kind === "MessageDeleted") {
        const p = evt.payload;
        if (p.channelPublicId !== channelPublicId) return;

        setMessages((prev) =>
          prev.map((m) => (m.id === p.id ? { ...m, isDeleted: true } : m)),
        );
      }
    },
    [channelPublicId],
  );

  useMessagesHub(hubHandler);

  // ---------------------------------------------------------------------------
  // Send message
  // ---------------------------------------------------------------------------

  const sendMessage = useCallback(
    async (content: string): Promise<void> => {
      if (!accessToken) return;
      const trimmed = content.trim();
      if (!trimmed) return;

      setSendError(null);
      setIsSending(true);

      // Optimistic: insert pending entry with negative temp ID
      const tempId = -Date.now();
      const pendingMsg: ChatMessage = {
        id: tempId,
        authorName: "",
        content: trimmed,
        sentAt: new Date().toISOString(),
        editedAt: null,
        viaDwbhub: true,
        discordMessageId: null, // filled after POST responds
        isDeleted: false,
        isPending: true,
      };

      setMessages((prev) => [...prev, pendingMsg]);

      try {
        const url = `/api/t/${encodeURIComponent(slug)}/channels/${encodeURIComponent(channelPublicId)}/messages`;
        const res = await fetch(url, {
          method: "POST",
          credentials: "include",
          headers: {
            Authorization: `Bearer ${accessToken}`,
            "Content-Type": "application/json",
          },
          body: JSON.stringify({ content: trimmed }),
        });

        if (!res.ok) {
          // Remove the pending entry on failure
          setMessages((prev) => prev.filter((m) => m.id !== tempId));
          setSendError("send");
          return;
        }

        const body = (await res.json()) as SendMessageResponse;

        // Stamp the pending entry with the real discordMessageId so the
        // incoming SignalR echo can find and replace it
        setMessages((prev) =>
          prev.map((m) =>
            m.id === tempId
              ? { ...m, discordMessageId: body.discordMessageId }
              : m,
          ),
        );
      } catch {
        setMessages((prev) => prev.filter((m) => m.id !== tempId));
        setSendError("send");
      } finally {
        setIsSending(false);
      }
    },
    [slug, channelPublicId, accessToken],
  );

  // ---------------------------------------------------------------------------
  // Bottom-tracking (for new-message badge)
  // ---------------------------------------------------------------------------

  const markAtBottom = useCallback((atBottom: boolean) => {
    atBottomRef.current = atBottom;
    if (atBottom) {
      setNewCount(0);
    }
  }, []);

  return {
    messages,
    isLoading,
    error,
    hasMore,
    isLoadingOlder,
    isSending,
    sendError,
    newCount,
    loadOlder,
    sendMessage,
    markAtBottom,
  };
}
