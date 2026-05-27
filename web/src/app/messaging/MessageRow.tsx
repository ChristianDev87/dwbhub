/**
 * MessageRow — renders a single chat message.
 *
 * Content is rendered as plain text (React's default escaping). No markdown
 * rendering library is used in this task — that is deferred to Plan 1.5.
 *
 * Deleted messages show a "[deleted]" placeholder (i18n key: chat.deleted).
 * Edited messages show an "(edited)" indicator (i18n key: chat.edited).
 * Pending messages are shown at reduced opacity while the POST is in flight.
 *
 * Edit/Delete action toolbar:
 *   - Visible on hover (CSS group/group-hover).
 *   - Edit: only shown when isOwnMessage=true and publicId is known.
 *   - Delete: shown when isOwnMessage=true OR isOwnerRole=true, and publicId is known.
 *   - Inline edit mode replaces the content area with a textarea.
 *   - Inline delete confirmation replaces the action toolbar with a confirm row.
 */

import type React from "react";
import { useState, useRef, useEffect } from "react";
import { useTranslation } from "react-i18next";
import type { ChatMessage, EditMessageError } from "./useChannel";

const MAX_EDIT_LENGTH = 1800;

interface MessageRowProps {
  message: ChatMessage;
  isOwnMessage: boolean;
  isOwnerRole: boolean;
  onEdit: (args: { messagePublicId: string; content: string }) => Promise<void>;
  onDelete: (args: { messagePublicId: string }) => Promise<void>;
  isEditing: boolean;
  isDeleting: boolean;
}

export function MessageRow({
  message,
  isOwnMessage,
  isOwnerRole,
  onEdit,
  onDelete,
  isEditing,
  isDeleting,
}: MessageRowProps): React.JSX.Element {
  const { t } = useTranslation();

  // Edit mode state — local to this row
  const [editMode, setEditMode] = useState(false);
  const [editValue, setEditValue] = useState("");
  const [editError, setEditError] = useState<string | null>(null);
  const [editPending, setEditPending] = useState(false);

  // Delete confirmation state — local to this row
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [deleteError, setDeleteError] = useState<string | null>(null);
  const [deletePending, setDeletePending] = useState(false);

  const textareaRef = useRef<HTMLTextAreaElement>(null);

  // Focus textarea when entering edit mode
  useEffect(() => {
    if (editMode && textareaRef.current) {
      textareaRef.current.focus();
      // Place cursor at end
      const len = textareaRef.current.value.length;
      textareaRef.current.setSelectionRange(len, len);
    }
  }, [editMode]);

  const sentDate = new Date(message.sentAt);
  const timeLabel = sentDate.toLocaleTimeString(undefined, {
    hour: "2-digit",
    minute: "2-digit",
  });

  // Permissions
  const canEdit =
    isOwnMessage && message.publicId !== null && !message.isDeleted;
  const canDelete =
    (isOwnMessage || isOwnerRole) &&
    message.publicId !== null &&
    !message.isDeleted;

  // ── Edit handlers ──────────────────────────────────────────────────────

  function handleEditStart() {
    setEditValue(message.content);
    setEditError(null);
    setEditMode(true);
  }

  function handleEditCancel() {
    setEditMode(false);
    setEditError(null);
  }

  async function handleEditSave() {
    const trimmed = editValue.trim();
    if (!trimmed) {
      setEditError(t("chat.editMessage.empty"));
      return;
    }
    if (trimmed.length > MAX_EDIT_LENGTH) {
      setEditError(t("chat.editMessage.tooLong"));
      return;
    }
    if (!message.publicId) return;

    setEditPending(true);
    setEditError(null);
    try {
      await onEdit({ messagePublicId: message.publicId, content: trimmed });
      setEditMode(false);
    } catch (err: unknown) {
      const editErr = err as EditMessageError;
      if (editErr.reason === "edit_window_expired") {
        setEditError(t("chat.editMessage.windowExpired"));
      } else if (editErr.reason === "forbidden") {
        setEditError(t("chat.editMessage.forbidden"));
      } else {
        setEditError(t("chat.editMessage.genericError"));
      }
    } finally {
      setEditPending(false);
    }
  }

  function handleEditKeyDown(e: React.KeyboardEvent<HTMLTextAreaElement>) {
    if (e.key === "Enter" && !e.shiftKey) {
      e.preventDefault();
      void handleEditSave();
    } else if (e.key === "Escape") {
      handleEditCancel();
    }
  }

  // ── Delete handlers ────────────────────────────────────────────────────

  function handleDeleteClick() {
    setDeleteError(null);
    setConfirmDelete(true);
  }

  function handleDeleteCancel() {
    setConfirmDelete(false);
    setDeleteError(null);
  }

  async function handleDeleteConfirm() {
    if (!message.publicId) return;
    setDeletePending(true);
    setDeleteError(null);
    try {
      await onDelete({ messagePublicId: message.publicId });
      setConfirmDelete(false);
    } catch {
      setDeleteError(t("chat.deleteMessage.genericError"));
    } finally {
      setDeletePending(false);
    }
  }

  // ── Validation feedback ────────────────────────────────────────────────

  const editTooLong = editValue.length > MAX_EDIT_LENGTH;
  const editEmpty = editValue.trim().length === 0;
  const editSaveDisabled = editPending || editTooLong || editEmpty;

  // ── Render ─────────────────────────────────────────────────────────────

  return (
    <div
      className={`group relative px-4 py-2 hover:bg-gray-50 ${message.isPending ? "opacity-60" : ""}`}
      data-testid={`message-row-${String(message.id)}`}
    >
      {/* Action toolbar — visible on hover (CSS group), hidden in edit mode */}
      {!editMode && !message.isDeleted && (canEdit || canDelete) && (
        <div
          className="absolute top-1 right-2 hidden group-hover:flex items-center gap-1 bg-white border border-gray-200 rounded shadow-sm px-1 py-0.5"
          data-testid="message-actions"
        >
          {canEdit && !confirmDelete && (
            <button
              type="button"
              className="text-xs text-gray-500 hover:text-blue-600 px-1.5 py-0.5 rounded hover:bg-blue-50"
              data-testid="message-edit-button"
              onClick={handleEditStart}
              disabled={isEditing || isDeleting}
            >
              {t("chat.editMessage.button")}
            </button>
          )}
          {canDelete && !confirmDelete && (
            <button
              type="button"
              className="text-xs text-gray-500 hover:text-red-600 px-1.5 py-0.5 rounded hover:bg-red-50"
              data-testid="message-delete-button"
              onClick={handleDeleteClick}
              disabled={isEditing || isDeleting}
            >
              {t("chat.deleteMessage.button")}
            </button>
          )}

          {/* Inline delete confirmation */}
          {confirmDelete && (
            <span
              className="flex items-center gap-1"
              data-testid="message-delete-confirm-row"
            >
              <span className="text-xs text-gray-600">
                {t("chat.deleteMessage.confirmPrompt")}
              </span>
              <button
                type="button"
                className="text-xs text-white bg-red-600 hover:bg-red-700 px-2 py-0.5 rounded"
                data-testid="message-delete-confirm-button"
                onClick={() => void handleDeleteConfirm()}
                disabled={deletePending}
              >
                {t("chat.deleteMessage.confirm")}
              </button>
              <button
                type="button"
                className="text-xs text-gray-500 hover:text-gray-700 px-1.5 py-0.5 rounded hover:bg-gray-100"
                data-testid="message-delete-cancel-button"
                onClick={handleDeleteCancel}
                disabled={deletePending}
              >
                {t("chat.deleteMessage.cancel")}
              </button>
            </span>
          )}
        </div>
      )}

      {/* Message header */}
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
      </div>

      {/* Content area — normal or edit mode */}
      {editMode ? (
        <div className="mt-1" data-testid="message-edit-area">
          <textarea
            ref={textareaRef}
            className={`w-full text-sm border rounded px-2 py-1 resize-none focus:outline-none focus:ring-1 ${
              editTooLong
                ? "border-red-400 focus:ring-red-400"
                : "border-blue-400 focus:ring-blue-400"
            }`}
            rows={3}
            maxLength={MAX_EDIT_LENGTH + 100} /* soft warn before hard cut */
            value={editValue}
            onChange={(e) => {
              setEditValue(e.target.value);
              setEditError(null);
            }}
            onKeyDown={handleEditKeyDown}
            data-testid="message-edit-textarea"
          />
          {/* Character count warning */}
          {editTooLong && (
            <p
              className="text-xs text-red-600 mt-0.5"
              data-testid="message-edit-toolong"
            >
              {t("chat.editMessage.tooLong")}
            </p>
          )}
          {/* Server error */}
          {editError && !editTooLong && (
            <p
              role="alert"
              className="text-xs text-red-600 mt-0.5"
              data-testid="message-edit-error"
            >
              {editError}
            </p>
          )}
          <div className="flex items-center gap-2 mt-1">
            <button
              type="button"
              className="text-xs bg-blue-600 text-white px-3 py-1 rounded hover:bg-blue-700 disabled:opacity-50"
              data-testid="message-edit-save"
              onClick={() => void handleEditSave()}
              disabled={editSaveDisabled}
            >
              {editPending ? "…" : t("chat.editMessage.save")}
            </button>
            <button
              type="button"
              className="text-xs text-gray-500 hover:text-gray-700 px-2 py-1 rounded hover:bg-gray-100"
              data-testid="message-edit-cancel"
              onClick={handleEditCancel}
              disabled={editPending}
            >
              {t("chat.editMessage.cancel")}
            </button>
            <span className="text-xs text-gray-400 ml-auto">
              {editValue.length}/{MAX_EDIT_LENGTH}
            </span>
          </div>
        </div>
      ) : (
        <p
          className={`text-sm mt-0.5 ${message.isDeleted ? "text-gray-400 italic" : "text-gray-700"} whitespace-pre-wrap break-words`}
          aria-label={message.isDeleted ? t("chat.deletedAria") : undefined}
          data-testid="message-content"
        >
          {message.isDeleted ? t("chat.deleted") : message.content}
        </p>
      )}

      {/* Delete error (shown below content area) */}
      {deleteError && (
        <p
          role="alert"
          className="text-xs text-red-600 mt-0.5"
          data-testid="message-delete-error"
        >
          {deleteError}
        </p>
      )}
    </div>
  );
}
