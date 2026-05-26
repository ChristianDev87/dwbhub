/**
 * MessageEditMode — inline edit textarea with char count and save/cancel.
 *
 * Behaviours:
 *   - Auto-focuses on mount with cursor at the end of existing content
 *   - Enter saves (Shift+Enter inserts newline)
 *   - Escape cancels
 *   - Save disabled when: content is empty, unchanged, or > 2000 chars
 */

import type React from "react";
import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

type Props = {
  initialContent: string;
  onSave: (newContent: string) => void;
  onCancel: () => void;
  saving?: boolean;
};

export function MessageEditMode({
  initialContent,
  onSave,
  onCancel,
  saving = false,
}: Props): React.JSX.Element {
  const { t } = useTranslation();
  const [content, setContent] = useState(initialContent);
  const textareaRef = useRef<HTMLTextAreaElement>(null);

  useEffect(() => {
    const el = textareaRef.current;
    if (el) {
      el.focus();
      el.setSelectionRange(initialContent.length, initialContent.length);
    }
  }, [initialContent]);

  const trimmed = content.trim();
  const canSave =
    trimmed.length > 0 &&
    trimmed.length <= 2000 &&
    trimmed !== initialContent.trim();

  function handleKeyDown(e: React.KeyboardEvent<HTMLTextAreaElement>) {
    if (e.key === "Enter" && !e.shiftKey) {
      e.preventDefault();
      if (canSave) onSave(trimmed);
    }
    if (e.key === "Escape") {
      e.preventDefault();
      onCancel();
    }
  }

  return (
    <div className="space-y-1 mt-1">
      <textarea
        ref={textareaRef}
        value={content}
        onChange={(e: React.ChangeEvent<HTMLTextAreaElement>) =>
          setContent(e.target.value)
        }
        onKeyDown={handleKeyDown}
        rows={3}
        className="w-full resize-none border border-gray-300 rounded px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:opacity-50"
        disabled={saving}
        data-testid="message-edit-textarea"
        aria-label={t("chat.menu.edit")}
      />
      <div className="flex justify-between items-center text-xs">
        <span
          className={`text-gray-400 ${content.length > 2000 ? "text-red-500" : ""}`}
          data-testid="message-edit-char-count"
        >
          {t("chat.editMode.charCount", { count: content.length })}
        </span>
        <div className="flex gap-2">
          <button
            type="button"
            onClick={onCancel}
            className="px-3 py-1 text-sm rounded hover:bg-gray-100 text-gray-600"
            data-testid="message-edit-cancel"
          >
            {t("chat.editMode.cancel")}
          </button>
          <button
            type="button"
            onClick={() => {
              if (canSave) onSave(trimmed);
            }}
            disabled={!canSave || saving}
            className="px-3 py-1 text-sm rounded bg-blue-600 text-white hover:bg-blue-700 disabled:opacity-50 disabled:cursor-not-allowed"
            data-testid="message-edit-save"
          >
            {t("chat.editMode.save")}
          </button>
        </div>
      </div>
    </div>
  );
}
