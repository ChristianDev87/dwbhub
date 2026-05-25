/**
 * MessageRow — renders a single chat message.
 *
 * Content is rendered as plain text (React's default escaping). No markdown
 * rendering library is used in this task — that is deferred to Plan 1.5.
 *
 * Deleted messages show a "[deleted]" placeholder (i18n key: chat.deleted).
 * Edited messages show an "(edited)" indicator (i18n key: chat.edited).
 * Pending messages are shown at reduced opacity while the POST is in flight.
 */

import type React from "react";
import { useTranslation } from "react-i18next";
import type { ChatMessage } from "./useChannel";

interface MessageRowProps {
  message: ChatMessage;
}

export function MessageRow({ message }: MessageRowProps): React.JSX.Element {
  const { t } = useTranslation();

  const sentDate = new Date(message.sentAt);
  const timeLabel = sentDate.toLocaleTimeString(undefined, {
    hour: "2-digit",
    minute: "2-digit",
  });

  return (
    <div
      className={`px-4 py-2 hover:bg-gray-50 ${message.isPending ? "opacity-60" : ""}`}
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
        {message.isPending && (
          <span className="text-xs text-gray-400" data-testid="message-pending">
            {t("chat.sending")}
          </span>
        )}
      </div>
      <p
        className={`text-sm mt-0.5 ${message.isDeleted ? "text-gray-400 italic" : "text-gray-700"} whitespace-pre-wrap break-words`}
        data-testid="message-content"
      >
        {message.isDeleted ? t("chat.deleted") : message.content}
      </p>
    </div>
  );
}
