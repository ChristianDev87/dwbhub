/**
 * ChatPage — full-page chat view for a single bridged channel.
 *
 * Channel name display (Option C — router state):
 *   When ChannelsPage navigates here via <Link state={{ channelName }}>, the
 *   channel name is available from useLocation().state. If the state is absent
 *   (deep-link / browser refresh), we fall back to the first 8 chars of the
 *   channelPublicId as a placeholder.
 *
 * TODO: add GET /api/t/{slug}/channels/{publicId} endpoint and fetch the
 *   channel name here so deep-links / refreshes also show the real name.
 *
 * Edit/Delete flow (Plan 1.1):
 *   - Permissions computed per-message (own outbound vs. moderator)
 *   - useChannel now exposes editMessage() and deleteMessage()
 *   - DeleteConfirmDialog handles the two-step confirmation
 *   - Error feedback is shown inline below the message list
 */

import type React from "react";
import { useCallback, useState } from "react";
import { useParams, useLocation, Link } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { HubConnectionState } from "@microsoft/signalr";
import { useAuth } from "../auth-context";
import {
  useChannel,
  type ChatMessage,
  type EditMessageError,
} from "./useChannel";
import { MessageList } from "./MessageList";
import { SendBox } from "./SendBox";
import { DeleteConfirmDialog } from "./DeleteConfirmDialog";
import type { MessageActionPermissions } from "./MessageActionsMenu";

export function ChatPage(): React.JSX.Element {
  const { slug, channelPublicId } = useParams<{
    slug: string;
    channelPublicId: string;
  }>();
  const location = useLocation();
  const { t } = useTranslation();
  const { state: authState } = useAuth();

  // Channel name via router state (Option C); fall back to ID prefix
  const channelName =
    (location.state as { channelName?: string } | null)?.channelName ??
    (channelPublicId ?? "").slice(0, 8);

  const {
    messages,
    isLoading,
    error,
    hasMore,
    isLoadingOlder,
    isSending,
    sendError,
    newCount,
    hubState,
    currentUserId,
    loadOlder,
    sendMessage,
    editMessage,
    deleteMessage,
    markAtBottom,
  } = useChannel(slug ?? "", channelPublicId ?? "");

  // Delete confirmation state
  const [deleteCandidateId, setDeleteCandidateId] = useState<number | null>(
    null,
  );
  // Inline error for edit/delete failures
  const [actionError, setActionError] = useState<string | null>(null);

  // -------------------------------------------------------------------------
  // Permission computation
  // -------------------------------------------------------------------------

  const isOwner =
    authState.kind === "authenticated" && authState.user.role === "Owner";

  const getPermissions = useCallback(
    (msg: ChatMessage): MessageActionPermissions => {
      const isOwnOutbound =
        msg.viaDwbhub &&
        msg.dwbhubUserId != null &&
        msg.dwbhubUserId === currentUserId;
      const isDeleted = msg.isDeleted;

      // Edit window: 15 minutes (no server-side config exposed to frontend yet;
      // backend enforces the real window — this is only for UI hint).
      const EDIT_WINDOW_MS = 15 * 60 * 1000;
      const ageSec = Date.now() - new Date(msg.sentAt).getTime();
      const withinEditWindow = ageSec < EDIT_WINDOW_MS;

      return {
        canEdit: isOwnOutbound && !isDeleted && withinEditWindow,
        canDeleteSelf: isOwnOutbound && !isDeleted,
        canModDelete: isOwner && !isOwnOutbound && !isDeleted,
      };
    },
    [currentUserId, isOwner],
  );

  // -------------------------------------------------------------------------
  // Edit handler
  // -------------------------------------------------------------------------

  const handleEdit = useCallback(
    async (messageId: number, content: string): Promise<void> => {
      setActionError(null);
      try {
        await editMessage(messageId, content);
      } catch (err: unknown) {
        const e = err as EditMessageError;
        if (e.status === 422 && e.code === "edit_window_expired") {
          const minutes = Math.ceil((e.windowSeconds ?? 0) / 60) || 15;
          setActionError(t("chat.error.editWindowExpired", { minutes }));
        } else if (e.status === 410) {
          setActionError(t("chat.error.alreadyDeleted"));
        } else if (e.status === 502) {
          setActionError(t("chat.error.discordUpstream"));
        } else {
          setActionError(t("chat.errorSend"));
        }
        // Re-throw so MessageEditMode knows the save failed and can stay open
        throw err;
      }
    },
    [editMessage, t],
  );

  // -------------------------------------------------------------------------
  // Delete handler
  // -------------------------------------------------------------------------

  const handleDeleteRequest = useCallback((messageId: number) => {
    setActionError(null);
    setDeleteCandidateId(messageId);
  }, []);

  const handleDeleteConfirm = useCallback(async () => {
    if (deleteCandidateId == null) return;
    const id = deleteCandidateId;
    setDeleteCandidateId(null);
    try {
      await deleteMessage(id);
    } catch (err: unknown) {
      const e = err as EditMessageError;
      if (e.status === 412) {
        setActionError(t("chat.error.botMissingPermission"));
      } else if (e.status === 410) {
        setActionError(t("chat.error.alreadyDeleted"));
      } else if (e.status === 502) {
        setActionError(t("chat.error.discordUpstream"));
      } else {
        setActionError(t("chat.errorSend"));
      }
    }
  }, [deleteCandidateId, deleteMessage, t]);

  // -------------------------------------------------------------------------
  // Render helpers
  // -------------------------------------------------------------------------

  if (isLoading) {
    return (
      <div className="flex flex-col h-screen max-w-3xl mx-auto">
        <header className="p-4 border-b border-gray-200 flex items-center gap-2">
          <BackLink slug={slug ?? ""} t={t} />
          <h1 className="text-lg font-semibold">#{channelName}</h1>
        </header>
        <div className="flex-1 flex items-center justify-center">
          <p className="text-gray-500" data-testid="chat-loading">
            {t("common.loading")}
          </p>
        </div>
      </div>
    );
  }

  if (error) {
    return (
      <div className="flex flex-col h-screen max-w-3xl mx-auto">
        <header className="p-4 border-b border-gray-200 flex items-center gap-2">
          <BackLink slug={slug ?? ""} t={t} />
          <h1 className="text-lg font-semibold">#{channelName}</h1>
        </header>
        <div className="flex-1 flex items-center justify-center">
          <p role="alert" className="text-red-600" data-testid="chat-error">
            {t("chat.errorLoad")}
          </p>
        </div>
      </div>
    );
  }

  return (
    <div
      className="flex flex-col h-screen max-w-3xl mx-auto"
      data-signalr-state={hubState.toString()}
    >
      {/* Header */}
      <header className="p-4 border-b border-gray-200 flex items-center gap-3 shrink-0">
        <BackLink slug={slug ?? ""} t={t} />
        <h1 className="text-lg font-semibold" data-testid="chat-channel-name">
          #{channelName}
        </h1>
      </header>

      {/* Older-messages loading indicator */}
      {isLoadingOlder && (
        <div className="text-center py-1 text-xs text-gray-400">
          {t("common.loading")}
        </div>
      )}

      {/* Message list or empty state */}
      <div className="flex-1 overflow-hidden relative">
        {messages.length === 0 ? (
          <div className="h-full flex items-center justify-center">
            <p className="text-gray-400 text-sm" data-testid="chat-empty">
              {t("chat.empty")}
            </p>
          </div>
        ) : (
          <MessageList
            messages={messages}
            hasMore={hasMore}
            onLoadOlder={loadOlder}
            onAtBottomChange={markAtBottom}
            getPermissions={getPermissions}
            onEdit={handleEdit}
            onDelete={handleDeleteRequest}
          />
        )}

        {/* New-messages badge */}
        {newCount > 0 && (
          <div className="absolute bottom-2 left-1/2 -translate-x-1/2">
            <span
              className="bg-blue-600 text-white text-xs px-3 py-1 rounded-full shadow"
              data-testid="chat-new-badge"
            >
              {t("chat.newMessages", { count: newCount })}
            </span>
          </div>
        )}
      </div>

      {/* Send error */}
      {sendError && (
        <p
          role="alert"
          className="px-4 py-1 text-sm text-red-600"
          data-testid="chat-send-error"
        >
          {t("chat.errorSend")}
        </p>
      )}

      {/* Edit / delete action error */}
      {actionError && (
        <p
          role="alert"
          className="px-4 py-1 text-sm text-red-600"
          data-testid="chat-action-error"
        >
          {actionError}
        </p>
      )}

      {/* Send box */}
      <div className="shrink-0">
        <SendBox onSend={sendMessage} disabled={isSending} />
      </div>

      {/* Delete confirmation dialog */}
      <DeleteConfirmDialog
        open={deleteCandidateId != null}
        onClose={() => setDeleteCandidateId(null)}
        onConfirm={() => void handleDeleteConfirm()}
      />
    </div>
  );
}

// ---------------------------------------------------------------------------
// Sub-components
// ---------------------------------------------------------------------------

function BackLink({
  slug,
  t,
}: {
  slug: string;
  t: (key: string) => string;
}): React.JSX.Element {
  return (
    <Link
      to={`/t/${slug}/guilds`}
      className="text-blue-600 hover:underline text-sm shrink-0"
      data-testid="chat-back-link"
    >
      {t("chat.back")}
    </Link>
  );
}

// Re-export HubConnectionState so callers can check connection status if needed
export { HubConnectionState };
