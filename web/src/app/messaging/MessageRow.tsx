/**
 * MessageRow — renders a single chat message.
 *
 * Content is rendered as plain text (React's default escaping). No markdown
 * rendering library is used in this task — that is deferred to Plan 1.5.
 *
 * Deleted messages show a context-sensitive placeholder based on deletedReason
 * (from the SignalR MessageDeleted event) and whether the message was sent by
 * the current user.
 *
 * Edited messages show an "(edited)" indicator.
 * Pending messages are shown at reduced opacity while the POST is in flight.
 *
 * When onEdit/onDelete are provided the row renders a hover-revealed 3-dot
 * menu. When editing is active the content area is replaced with MessageEditMode.
 */

import type React from "react";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import type { ChatMessage } from "./useChannel";
import type { MessageActionPermissions } from "./MessageActionsMenu";
import { MessageActionsMenu } from "./MessageActionsMenu";
import { MessageEditMode } from "./MessageEditMode";

interface MessageRowProps {
  message: ChatMessage;
  permissions?: MessageActionPermissions;
  onEdit?: (messageId: number, content: string) => Promise<void>;
  onDelete?: (messageId: number) => void;
}

export function MessageRow({
  message,
  permissions,
  onEdit,
  onDelete,
}: MessageRowProps): React.JSX.Element {
  const { t } = useTranslation();
  const [isEditing, setIsEditing] = useState(false);
  const [isSaving, setIsSaving] = useState(false);

  const sentDate = new Date(message.sentAt);
  const timeLabel = sentDate.toLocaleTimeString(undefined, {
    hour: "2-digit",
    minute: "2-digit",
  });

  // Deleted-message placeholder text
  function getDeletedText(): string {
    if (message.deletedReason === "moderation") {
      return t("chat.deletedPlaceholder.moderation");
    }
    // "self" deletedReason means the dwbhub user deleted their own message
    if (message.deletedReason === "self") {
      return t("chat.deletedPlaceholder.self");
    }
    // Fallback: deleted on Discord side (no reason / discord_side)
    return t("chat.deletedPlaceholder.discordSide");
  }

  const showActionsMenu =
    !message.isDeleted &&
    !message.isPending &&
    permissions != null &&
    (permissions.canEdit ||
      permissions.canDeleteSelf ||
      permissions.canModDelete);

  async function handleSave(newContent: string) {
    if (!onEdit) return;
    setIsSaving(true);
    try {
      await onEdit(message.id, newContent);
      setIsEditing(false);
    } finally {
      setIsSaving(false);
    }
  }

  return (
    <div
      className={`group px-4 py-2 hover:bg-gray-50 ${message.isPending ? "opacity-60" : ""}`}
      data-testid={`message-row-${String(message.id)}`}
    >
      <div className="flex items-baseline gap-2">
        <span
          className="font-semibold text-sm text-gray-800"
          data-testid="message-author"
        >
          {message.isDeleted ? "" : message.authorName || t("chat.you")}
        </span>
        <time
          dateTime={message.sentAt}
          className="text-xs text-gray-400"
          data-testid="message-time"
        >
          {timeLabel}
        </time>
        {message.editedAt && !message.isDeleted && (
          <span
            className="text-xs text-gray-400 italic"
            data-testid="message-edited"
          >
            ({t("chat.edited")})
          </span>
        )}
        {message.viaDwbhub && !message.isDeleted && (
          <span
            className="text-xs text-gray-400"
            data-testid="message-via-dwbhub"
          >
            {t("chat.viaDwbhub")}
          </span>
        )}
        {message.isPending && (
          <span className="text-xs text-gray-400" data-testid="message-pending">
            {t("chat.sending")}
          </span>
        )}

        {/* Actions menu — appears on hover via group-hover in MessageActionsMenu */}
        {showActionsMenu && permissions && (
          <div className="ml-auto flex-shrink-0">
            <MessageActionsMenu
              canEdit={permissions.canEdit}
              canDeleteSelf={permissions.canDeleteSelf}
              canModDelete={permissions.canModDelete}
              onEdit={() => setIsEditing(true)}
              onDelete={() => onDelete?.(message.id)}
            />
          </div>
        )}
      </div>

      {isEditing ? (
        <MessageEditMode
          initialContent={message.content}
          onSave={(newContent) => void handleSave(newContent)}
          onCancel={() => setIsEditing(false)}
          saving={isSaving}
        />
      ) : (
        <p
          className={`text-sm mt-0.5 ${message.isDeleted ? "text-gray-400 italic" : "text-gray-700"} whitespace-pre-wrap break-words`}
          aria-label={message.isDeleted ? t("chat.deletedAria") : undefined}
          data-testid="message-content"
        >
          {message.isDeleted ? getDeletedText() : message.content}
        </p>
      )}
    </div>
  );
}
