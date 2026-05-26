/**
 * DeleteConfirmDialog — modal dialog to confirm message deletion.
 *
 * Uses a plain backdrop + centered card pattern matching the project's
 * existing modal conventions (BotTokenModal, PauseGuildModal).
 */

import type React from "react";
import { useTranslation } from "react-i18next";

type Props = {
  open: boolean;
  onClose: () => void;
  onConfirm: () => void;
};

export function DeleteConfirmDialog({
  open,
  onClose,
  onConfirm,
}: Props): React.JSX.Element | null {
  const { t } = useTranslation();

  if (!open) return null;

  function handleBackdrop(e: React.MouseEvent<HTMLDivElement>) {
    if (e.target === e.currentTarget) onClose();
  }

  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/50"
      role="dialog"
      aria-modal="true"
      aria-labelledby="delete-dialog-title"
      onClick={handleBackdrop}
      data-testid="delete-dialog-backdrop"
    >
      <div className="bg-white rounded-lg shadow-xl p-6 w-full max-w-sm mx-4">
        <h2
          id="delete-dialog-title"
          className="text-base font-semibold text-gray-900 mb-2"
        >
          {t("chat.deleteDialog.title")}
        </h2>
        <p className="text-sm text-gray-600 mb-5">
          {t("chat.deleteDialog.body")}
        </p>
        <div className="flex justify-end gap-3">
          <button
            type="button"
            onClick={onClose}
            className="px-4 py-2 text-sm rounded border border-gray-300 hover:bg-gray-50"
            data-testid="delete-dialog-cancel"
          >
            {t("chat.deleteDialog.cancel")}
          </button>
          <button
            type="button"
            onClick={onConfirm}
            className="px-4 py-2 text-sm rounded bg-red-600 text-white hover:bg-red-700"
            data-testid="delete-dialog-confirm"
          >
            {t("chat.deleteDialog.confirm")}
          </button>
        </div>
      </div>
    </div>
  );
}
