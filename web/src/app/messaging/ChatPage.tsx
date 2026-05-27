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
 */

import type React from "react";
import { useParams, useLocation, Link } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { HubConnectionState } from "@microsoft/signalr";
import { useAuth } from "../auth-context";
import { useChannel } from "./useChannel";
import { MessageList } from "./MessageList";
import { SendBox } from "./SendBox";

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

  // Current user info for permission checks
  const currentUserDisplayName =
    authState.kind === "authenticated" ? authState.user.displayName : "";
  const isOwnerRole =
    authState.kind === "authenticated" && authState.user.role === "Owner";

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
    loadOlder,
    sendMessage,
    markAtBottom,
    editMessage,
    deleteMessage,
    isEditing,
    isDeleting,
  } = useChannel(slug ?? "", channelPublicId ?? "");

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
            currentUserDisplayName={currentUserDisplayName}
            isOwnerRole={isOwnerRole}
            onEdit={editMessage}
            onDelete={deleteMessage}
            isEditing={isEditing}
            isDeleting={isDeleting}
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

      {/* Send box */}
      <div className="shrink-0">
        <SendBox onSend={sendMessage} disabled={isSending} />
      </div>
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
