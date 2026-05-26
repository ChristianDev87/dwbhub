/**
 * MessageActionsMenu — hover-revealed 3-dot context menu for message actions.
 *
 * Uses a simple CSS-only / state-driven dropdown (no external component library)
 * that matches the project's existing Tailwind / plain-HTML convention.
 *
 * The trigger has `opacity-0 group-hover:opacity-100` so it appears only when
 * the parent container carries the `group` class. The menu is closed on blur or
 * when any item is clicked.
 */

import type React from "react";
import { useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { MoreHorizontal } from "lucide-react";

export type MessageActionPermissions = {
  canEdit: boolean;
  canDeleteSelf: boolean;
  canModDelete: boolean;
};

type Props = MessageActionPermissions & {
  onEdit: () => void;
  onDelete: () => void;
};

export function MessageActionsMenu({
  canEdit,
  canDeleteSelf,
  canModDelete,
  onEdit,
  onDelete,
}: Props): React.JSX.Element | null {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const menuRef = useRef<HTMLDivElement>(null);

  if (!canEdit && !canDeleteSelf && !canModDelete) return null;

  const isModDelete = canModDelete && !canDeleteSelf;

  function handleTrigger() {
    setOpen((v) => !v);
  }

  function handleBlur(e: React.FocusEvent<HTMLDivElement>) {
    // Close when focus leaves the menu wrapper entirely
    if (!menuRef.current?.contains(e.relatedTarget as Node | null)) {
      setOpen(false);
    }
  }

  function handleEdit() {
    setOpen(false);
    onEdit();
  }

  function handleDelete() {
    setOpen(false);
    onDelete();
  }

  return (
    <div
      ref={menuRef}
      className="relative"
      onBlur={handleBlur}
      data-testid="message-actions-wrapper"
    >
      <button
        type="button"
        onClick={handleTrigger}
        className="opacity-0 group-hover:opacity-100 focus:opacity-100 transition-opacity rounded p-1 hover:bg-gray-100"
        aria-label={t("chat.menu.edit")}
        aria-haspopup="true"
        aria-expanded={open}
        data-testid="message-actions-trigger"
      >
        <MoreHorizontal className="h-4 w-4 text-gray-500" />
      </button>

      {open && (
        <div
          className="absolute right-0 top-full mt-1 z-10 min-w-[10rem] rounded-md border border-gray-200 bg-white shadow-md py-1"
          role="menu"
          data-testid="message-actions-menu"
        >
          {canEdit && (
            <button
              type="button"
              role="menuitem"
              onClick={handleEdit}
              className="w-full text-left px-4 py-2 text-sm text-gray-700 hover:bg-gray-50"
              data-testid="message-actions-edit"
            >
              {t("chat.menu.edit")}
            </button>
          )}
          {(canDeleteSelf || canModDelete) && (
            <button
              type="button"
              role="menuitem"
              onClick={handleDelete}
              className="w-full text-left px-4 py-2 text-sm text-red-600 hover:bg-gray-50"
              data-testid="message-actions-delete"
            >
              {isModDelete
                ? t("chat.menu.moderationDelete")
                : t("chat.menu.delete")}
            </button>
          )}
        </div>
      )}
    </div>
  );
}
