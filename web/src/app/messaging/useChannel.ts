/**
 * useChannel — chat history + live updates + send for a single channel.
 *
 * Migrated to typed openapi-fetch + TanStack Query (PR 5c).
 *
 * Responsibilities:
 *   - Initial fetch of message history (newest-first, then reversed for display)
 *   - Lazy-load of older pages via loadOlder() — manual cache extension via
 *     setQueryData (not useInfiniteQuery, to keep SignalR patch paths simple)
 *   - Live SignalR events: MessageReceived, MessageUpdated, MessageDeleted
 *   - Optimistic send with echo deduplication by discordMessageId; falls back
 *     to content+authorName match when the POST has not yet resolved (race
 *     case: server broadcasts before the HTTP response reaches the client)
 *   - Optimistic edit (PATCH) and delete (DELETE) with rollback on error
 */

import { useCallback, useRef, useState } from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { type HubConnectionState } from "@microsoft/signalr";
import { useAuth } from "../auth-context";
import { useApiClient } from "@/lib/api/useApiClient";
import { qk } from "@/lib/api/queryKeys";
import { useMessagesHub } from "./useMessagesHub";
import type { MessageEvent } from "./messages-events";
import type { components } from "@/lib/api/generated/schema";

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

type SchemaMessageItem = components["schemas"]["MessageHistoryItem"];
type SchemaMessageHistoryResponse =
  components["schemas"]["MessageHistoryResponse"];

export interface ChatMessage {
  /** Positive from backend; negative temp ID for pending sends. */
  id: number;
  /**
   * Server-assigned UUID for this message. Required for PATCH/DELETE requests.
   * - populated from MessageHistoryItem.publicId for history messages.
   * - null while a POST is in-flight (pending send).
   * - populated from SendMessageResponse.publicId once the POST resolves.
   * - populated from EditMessageResponse.publicId after a successful PATCH.
   */
  publicId: string | null;
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

// Internal cache shape (extends the API response with display-ready messages)
interface MessageCacheData {
  messages: ChatMessage[];
  nextBefore: number | null;
}

// Discriminated error shape thrown by editMessage() mutation
export type EditMessageError =
  | { reason: "edit_window_expired" }
  | { reason: "forbidden" }
  | { reason: "generic" };

export interface UseChannelResult {
  messages: ChatMessage[];
  isLoading: boolean;
  error: string | null;
  hasMore: boolean;
  isLoadingOlder: boolean;
  isSending: boolean;
  sendError: string | null;
  newCount: number;
  /** Current SignalR hub connection state. Used by ChatPage for data-signalr-state. */
  hubState: HubConnectionState;
  loadOlder: () => void;
  sendMessage: (content: string) => Promise<void>;
  markAtBottom: (atBottom: boolean) => void;
  /** Optimistic PATCH — throws EditMessageError on failure. */
  editMessage: (args: {
    messagePublicId: string;
    content: string;
  }) => Promise<void>;
  /** Optimistic DELETE — throws on failure (rolls back optimistic state). */
  deleteMessage: (args: { messagePublicId: string }) => Promise<void>;
  isEditing: boolean;
  isDeleting: boolean;
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function schemaItemToChat(item: SchemaMessageItem): ChatMessage {
  return {
    id: item.id ?? 0,
    publicId: item.publicId ?? null,
    authorName: item.authorName ?? "",
    content: item.content ?? "",
    sentAt: item.sentAt ?? new Date().toISOString(),
    editedAt: item.editedAt ?? null,
    viaDwbhub: item.viaDwbhub ?? false,
    discordMessageId: item.discordMessageId ?? null,
    isDeleted: false,
    isPending: false,
  };
}

// ---------------------------------------------------------------------------
// Hook
// ---------------------------------------------------------------------------

export function useChannel(
  slug: string,
  channelPublicId: string,
): UseChannelResult {
  const { state } = useAuth();
  const displayName =
    state.kind === "authenticated" ? state.user.displayName : "";

  const api = useApiClient();
  const queryClient = useQueryClient();

  // Track whether the user is at the bottom of the list
  const atBottomRef = useRef(true);

  // Local state for things that don't belong in the query cache
  const [isLoadingOlder, setIsLoadingOlder] = useState(false);
  const [newCount, setNewCount] = useState(0);

  // ── Initial message query ────────────────────────────────────────────────
  const {
    data: cacheData,
    isLoading,
    isError,
  } = useQuery<MessageCacheData>({
    queryKey: qk.messages.list(slug, channelPublicId),
    queryFn: async () => {
      const { data, error } = await api.GET(
        "/api/t/{slug}/channels/{channelPublicId}/messages",
        {
          params: {
            path: { slug, channelPublicId },
            query: { limit: 50 },
          },
        },
      );
      if (error) throw error;
      const resp = data as SchemaMessageHistoryResponse;
      // API returns newest-first; reverse for display (oldest at top)
      const messages: ChatMessage[] = [...(resp.messages ?? [])]
        .reverse()
        .map(schemaItemToChat);
      return {
        messages,
        nextBefore: resp.nextBefore ?? null,
      };
    },
    staleTime: Infinity, // SignalR owns freshness
    enabled: slug.length > 0 && channelPublicId.length > 0,
  });

  const messages = cacheData?.messages ?? [];
  const hasMore = (cacheData?.nextBefore ?? null) !== null;

  // ── Load older messages (scroll-to-top pagination) ───────────────────────
  const loadOlder = useCallback(() => {
    const current = queryClient.getQueryData<MessageCacheData>(
      qk.messages.list(slug, channelPublicId),
    );
    if (!current || current.nextBefore === null || isLoadingOlder) return;

    const beforeCursor = current.nextBefore;
    setIsLoadingOlder(true);

    void (async () => {
      try {
        const { data, error } = await api.GET(
          "/api/t/{slug}/channels/{channelPublicId}/messages",
          {
            params: {
              path: { slug, channelPublicId },
              query: { limit: 50, before: beforeCursor },
            },
          },
        );
        if (error) return;
        const resp = data as SchemaMessageHistoryResponse;
        const older: ChatMessage[] = [...(resp.messages ?? [])]
          .reverse()
          .map(schemaItemToChat);

        queryClient.setQueryData<MessageCacheData>(
          qk.messages.list(slug, channelPublicId),
          (old) => {
            if (!old) return old;
            return {
              messages: [...older, ...old.messages],
              nextBefore: resp.nextBefore ?? null,
            };
          },
        );
      } finally {
        setIsLoadingOlder(false);
      }
    })();
  }, [api, queryClient, slug, channelPublicId, isLoadingOlder]);

  // ── Optimistic send mutation ─────────────────────────────────────────────
  const sendMutation = useMutation<
    components["schemas"]["SendMessageResponse"],
    Error,
    string,
    { previousData: MessageCacheData | undefined; tempId: number }
  >({
    mutationFn: async (content: string) => {
      const { data, error } = await api.POST(
        "/api/t/{slug}/channels/{channelPublicId}/messages",
        {
          params: { path: { slug, channelPublicId } },
          body: { content },
        },
      );
      if (error) throw new Error("send");
      return data as components["schemas"]["SendMessageResponse"];
    },
    onMutate: async (content: string) => {
      await queryClient.cancelQueries({
        queryKey: qk.messages.list(slug, channelPublicId),
      });
      const previousData = queryClient.getQueryData<MessageCacheData>(
        qk.messages.list(slug, channelPublicId),
      );

      const tempId = -Date.now();
      const pendingMsg: ChatMessage = {
        id: tempId,
        publicId: null,
        authorName: displayName,
        content,
        sentAt: new Date().toISOString(),
        editedAt: null,
        viaDwbhub: true,
        discordMessageId: null, // filled after POST responds
        isDeleted: false,
        isPending: true,
      };

      queryClient.setQueryData<MessageCacheData>(
        qk.messages.list(slug, channelPublicId),
        (old) => {
          if (!old) return old;
          return {
            ...old,
            messages: [...old.messages, pendingMsg],
          };
        },
      );

      return { previousData, tempId };
    },
    onError: (_err, _content, ctx) => {
      // Rollback optimistic entry
      if (ctx?.previousData !== undefined) {
        queryClient.setQueryData(
          qk.messages.list(slug, channelPublicId),
          ctx.previousData,
        );
      }
    },
    onSuccess: (responseData, _content, ctx) => {
      if (!ctx) return;
      const { tempId } = ctx;
      // Stamp the pending entry with the real discordMessageId and publicId so
      // the incoming SignalR echo can find and replace it. The publicId enables
      // Edit/Delete actions on this message in the current session.
      queryClient.setQueryData<MessageCacheData>(
        qk.messages.list(slug, channelPublicId),
        (old) => {
          if (!old) return old;
          return {
            ...old,
            messages: old.messages.map((m) =>
              m.id === tempId
                ? {
                    ...m,
                    publicId: responseData.publicId ?? null,
                    discordMessageId: responseData.discordMessageId ?? null,
                  }
                : m,
            ),
          };
        },
      );
    },
  });

  async function sendMessage(content: string): Promise<void> {
    const trimmed = content.trim();
    if (!trimmed) return;
    await sendMutation.mutateAsync(trimmed);
  }

  // ── Optimistic edit mutation ─────────────────────────────────────────────
  const editMutation = useMutation<
    components["schemas"]["EditMessageResponse"],
    EditMessageError,
    { messagePublicId: string; content: string },
    { previous: MessageCacheData | undefined }
  >({
    mutationFn: async ({ messagePublicId, content }) => {
      const { data, error } = await api.PATCH(
        "/api/t/{slug}/channels/{channelPublicId}/messages/{messagePublicId}",
        {
          params: {
            path: { slug, channelPublicId, messagePublicId },
          },
          body: { content },
        },
      );
      if (error) {
        // openapi-fetch surfaces HTTP error bodies in `error`
        const maybeExpired = error as {
          error?: string;
          status?: number;
        };
        if (
          maybeExpired.error === "edit_window_expired" ||
          // Some error shapes carry status directly
          (error as { status?: number }).status === 422
        ) {
          throw { reason: "edit_window_expired" } satisfies EditMessageError;
        }
        const maybeStatus = error as { status?: number };
        if (maybeStatus.status === 403) {
          throw { reason: "forbidden" } satisfies EditMessageError;
        }
        throw { reason: "generic" } satisfies EditMessageError;
      }
      return data as components["schemas"]["EditMessageResponse"];
    },
    onMutate: async ({ messagePublicId, content }) => {
      await queryClient.cancelQueries({
        queryKey: qk.messages.list(slug, channelPublicId),
      });
      const previous = queryClient.getQueryData<MessageCacheData>(
        qk.messages.list(slug, channelPublicId),
      );
      // Optimistic update: patch content and mark as edited
      queryClient.setQueryData<MessageCacheData>(
        qk.messages.list(slug, channelPublicId),
        (old) => {
          if (!old) return old;
          return {
            ...old,
            messages: old.messages.map((m) =>
              m.publicId === messagePublicId
                ? { ...m, content, editedAt: new Date().toISOString() }
                : m,
            ),
          };
        },
      );
      return { previous };
    },
    onError: (_err, _input, ctx) => {
      if (ctx?.previous !== undefined) {
        queryClient.setQueryData(
          qk.messages.list(slug, channelPublicId),
          ctx.previous,
        );
      }
    },
    // onSuccess: SignalR MessageUpdated will ratify the optimistic state with
    // the server-canonical content + editedAt values. No extra step needed.
  });

  async function editMessage(args: {
    messagePublicId: string;
    content: string;
  }): Promise<void> {
    await editMutation.mutateAsync(args);
  }

  // ── Optimistic delete mutation ───────────────────────────────────────────
  const deleteMutation = useMutation<
    void,
    EditMessageError,
    { messagePublicId: string },
    { previous: MessageCacheData | undefined }
  >({
    mutationFn: async ({ messagePublicId }) => {
      const { error } = await api.DELETE(
        "/api/t/{slug}/channels/{channelPublicId}/messages/{messagePublicId}",
        {
          params: {
            path: { slug, channelPublicId, messagePublicId },
          },
        },
      );
      if (error) {
        const maybeStatus = error as { status?: number };
        if (maybeStatus.status === 403) {
          throw { reason: "forbidden" } satisfies EditMessageError;
        }
        throw { reason: "generic" } satisfies EditMessageError;
      }
    },
    onMutate: async ({ messagePublicId }) => {
      await queryClient.cancelQueries({
        queryKey: qk.messages.list(slug, channelPublicId),
      });
      const previous = queryClient.getQueryData<MessageCacheData>(
        qk.messages.list(slug, channelPublicId),
      );
      // Optimistic update: flip isDeleted flag
      queryClient.setQueryData<MessageCacheData>(
        qk.messages.list(slug, channelPublicId),
        (old) => {
          if (!old) return old;
          return {
            ...old,
            messages: old.messages.map((m) =>
              m.publicId === messagePublicId ? { ...m, isDeleted: true } : m,
            ),
          };
        },
      );
      return { previous };
    },
    onError: (_err, _input, ctx) => {
      if (ctx?.previous !== undefined) {
        queryClient.setQueryData(
          qk.messages.list(slug, channelPublicId),
          ctx.previous,
        );
      }
    },
    // onSuccess: SignalR MessageDeleted will ratify the optimistic state.
  });

  async function deleteMessage(args: {
    messagePublicId: string;
  }): Promise<void> {
    await deleteMutation.mutateAsync(args);
  }

  // ── Live SignalR events ──────────────────────────────────────────────────
  const hubHandler = useCallback(
    (evt: MessageEvent) => {
      if (evt.kind === "MessageReceived") {
        const p = evt.payload;
        if (p.channelPublicId !== channelPublicId) return;

        queryClient.setQueryData<MessageCacheData>(
          qk.messages.list(slug, channelPublicId),
          (old) => {
            if (!old) return old;
            const prev = old.messages;

            // Match 1: pending entry already has discordMessageId (POST resolved first)
            let pendingIdx = prev.findIndex(
              (m) =>
                m.isPending &&
                m.discordMessageId !== null &&
                String(m.discordMessageId) === String(p.discordMessageId),
            );

            // Match 2: race case — POST has not resolved yet so discordMessageId
            // is still null on the pending entry; fall back to content + authorName
            if (pendingIdx === -1 && p.viaDwbhub) {
              pendingIdx = prev.findIndex(
                (m) =>
                  m.isPending &&
                  m.discordMessageId === null &&
                  m.content === p.content &&
                  m.authorName === p.authorName,
              );
            }

            if (pendingIdx !== -1) {
              // Replace pending entry with confirmed message
              const updated = [...prev];
              updated[pendingIdx] = {
                id: p.id,
                publicId: (p as { publicId?: string }).publicId ?? null,
                authorName: p.authorName,
                content: p.content,
                sentAt: p.sentAt,
                editedAt: null,
                viaDwbhub: p.viaDwbhub,
                discordMessageId: p.discordMessageId,
                isDeleted: false,
                isPending: false,
              };
              return { ...old, messages: updated };
            }

            // No match: new message from someone else (or unmatched — append)
            const newMsg: ChatMessage = {
              id: p.id,
              publicId: (p as { publicId?: string }).publicId ?? null,
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

            return { ...old, messages: [...prev, newMsg] };
          },
        );
      } else if (evt.kind === "MessageUpdated") {
        const p = evt.payload;
        queryClient.setQueryData<MessageCacheData>(
          qk.messages.list(slug, channelPublicId),
          (old) => {
            if (!old) return old;
            return {
              ...old,
              messages: old.messages.map((m) =>
                m.discordMessageId !== null &&
                String(m.discordMessageId) === String(p.messageId)
                  ? { ...m, content: p.content, editedAt: p.editedAt }
                  : m,
              ),
            };
          },
        );
      } else if (evt.kind === "MessageDeleted") {
        const p = evt.payload;
        queryClient.setQueryData<MessageCacheData>(
          qk.messages.list(slug, channelPublicId),
          (old) => {
            if (!old) return old;
            return {
              ...old,
              messages: old.messages.map((m) =>
                m.discordMessageId !== null &&
                String(m.discordMessageId) === String(p.messageId)
                  ? { ...m, isDeleted: true }
                  : m,
              ),
            };
          },
        );
      }
    },
    [queryClient, slug, channelPublicId],
  );

  const hubState = useMessagesHub(hubHandler);

  // ── Bottom-tracking (for new-message badge) ──────────────────────────────
  const markAtBottom = useCallback((atBottom: boolean) => {
    atBottomRef.current = atBottom;
    if (atBottom) {
      setNewCount(0);
    }
  }, []);

  return {
    messages,
    isLoading,
    error: isError ? "load" : null,
    hasMore,
    isLoadingOlder,
    isSending: sendMutation.isPending,
    sendError: sendMutation.isError ? "send" : null,
    newCount,
    hubState,
    loadOlder,
    sendMessage,
    markAtBottom,
    editMessage,
    deleteMessage,
    isEditing: editMutation.isPending,
    isDeleting: deleteMutation.isPending,
  };
}
