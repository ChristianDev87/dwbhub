/**
 * SendBox — text input for composing and sending messages.
 *
 * Behaviours:
 *   - Enter sends (Shift+Enter inserts a newline)
 *   - Empty / whitespace-only input does NOT send
 *   - Max 2000 characters (mirrors API validation)
 *   - Disabled while a send is in flight (prevents duplicate sends)
 *   - Calls onSend with the trimmed content; caller owns state reset
 */

import type React from "react";
import { useRef, useState } from "react";
import { useTranslation } from "react-i18next";

interface SendBoxProps {
  onSend: (content: string) => Promise<void>;
  disabled?: boolean;
}

export function SendBox({
  onSend,
  disabled = false,
}: SendBoxProps): React.JSX.Element {
  const { t } = useTranslation();
  const [value, setValue] = useState("");
  const isSendingRef = useRef(false);

  async function submit() {
    const trimmed = value.trim();
    if (!trimmed || disabled || isSendingRef.current) return;

    isSendingRef.current = true;
    setValue("");
    try {
      await onSend(trimmed);
    } finally {
      isSendingRef.current = false;
    }
  }

  function handleKeyDown(e: React.KeyboardEvent<HTMLTextAreaElement>) {
    if (e.key === "Enter" && !e.shiftKey) {
      e.preventDefault();
      void submit();
    }
  }

  return (
    <div className="border-t border-gray-200 px-4 py-3 flex gap-2 items-end bg-white">
      <textarea
        className="flex-1 resize-none border border-gray-300 rounded px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:opacity-50"
        rows={1}
        maxLength={2000}
        value={value}
        onChange={(e: React.ChangeEvent<HTMLTextAreaElement>) =>
          setValue(e.target.value)
        }
        onKeyDown={handleKeyDown}
        disabled={disabled}
        placeholder={t("chat.placeholder")}
        data-testid="send-box-input"
        aria-label={t("chat.composeAria")}
      />
      <button
        type="button"
        className="px-4 py-2 bg-blue-600 text-white text-sm font-medium rounded hover:bg-blue-700 disabled:opacity-50 disabled:cursor-not-allowed"
        onClick={() => void submit()}
        disabled={disabled || !value.trim()}
        data-testid="send-box-button"
      >
        {t("chat.send")}
      </button>
    </div>
  );
}
