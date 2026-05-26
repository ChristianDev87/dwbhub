/**
 * MessageActionsMenu unit tests (Plan 1.1 Task 10)
 */

import { describe, it, expect, vi } from "vitest";
import { render, screen, fireEvent } from "@testing-library/react";
import { I18nextProvider } from "react-i18next";
import { i18n } from "../../src/lib/i18n";
import { MessageActionsMenu } from "../../src/app/messaging/MessageActionsMenu";

function renderMenu(props: {
  canEdit: boolean;
  canDeleteSelf: boolean;
  canModDelete: boolean;
  onEdit?: () => void;
  onDelete?: () => void;
}) {
  void i18n.changeLanguage("en");
  return render(
    <I18nextProvider i18n={i18n}>
      <MessageActionsMenu
        canEdit={props.canEdit}
        canDeleteSelf={props.canDeleteSelf}
        canModDelete={props.canModDelete}
        onEdit={props.onEdit ?? vi.fn()}
        onDelete={props.onDelete ?? vi.fn()}
      />
    </I18nextProvider>,
  );
}

describe("MessageActionsMenu", () => {
  it("renders nothing when user has no permissions", () => {
    const { container } = renderMenu({
      canEdit: false,
      canDeleteSelf: false,
      canModDelete: false,
    });
    expect(container.firstChild).toBeNull();
  });

  it("shows trigger when user has at least one permission", () => {
    renderMenu({ canEdit: true, canDeleteSelf: false, canModDelete: false });
    expect(screen.getByTestId("message-actions-trigger")).toBeInTheDocument();
  });

  it("shows edit and delete options for own outbound message", () => {
    renderMenu({ canEdit: true, canDeleteSelf: true, canModDelete: false });
    const trigger = screen.getByTestId("message-actions-trigger");
    fireEvent.click(trigger);
    expect(screen.getByTestId("message-actions-edit")).toBeInTheDocument();
    expect(screen.getByTestId("message-actions-delete")).toBeInTheDocument();
    // Delete label is "Delete", not "Delete (Moderation)"
    expect(screen.getByTestId("message-actions-delete")).toHaveTextContent(
      "Delete",
    );
  });

  it("shows mod-delete label for owner on others outbound messages", () => {
    renderMenu({ canEdit: false, canDeleteSelf: false, canModDelete: true });
    const trigger = screen.getByTestId("message-actions-trigger");
    fireEvent.click(trigger);
    expect(
      screen.queryByTestId("message-actions-edit"),
    ).not.toBeInTheDocument();
    const deleteBtn = screen.getByTestId("message-actions-delete");
    expect(deleteBtn).toHaveTextContent("Delete (Moderation)");
  });

  it("shows mod-delete for owner on inbound when bot has permission", () => {
    // canModDelete=true covers inbound (non-viaDwbhub) messages when bot can manage
    renderMenu({ canEdit: false, canDeleteSelf: false, canModDelete: true });
    const trigger = screen.getByTestId("message-actions-trigger");
    fireEvent.click(trigger);
    expect(screen.getByTestId("message-actions-delete")).toHaveTextContent(
      "Delete (Moderation)",
    );
  });

  it("does not render delete option when both canDeleteSelf and canModDelete are false", () => {
    renderMenu({ canEdit: true, canDeleteSelf: false, canModDelete: false });
    const trigger = screen.getByTestId("message-actions-trigger");
    fireEvent.click(trigger);
    expect(
      screen.queryByTestId("message-actions-delete"),
    ).not.toBeInTheDocument();
  });

  it("calls onEdit when edit item is clicked", () => {
    const onEdit = vi.fn();
    renderMenu({
      canEdit: true,
      canDeleteSelf: true,
      canModDelete: false,
      onEdit,
    });
    fireEvent.click(screen.getByTestId("message-actions-trigger"));
    fireEvent.click(screen.getByTestId("message-actions-edit"));
    expect(onEdit).toHaveBeenCalledOnce();
  });

  it("calls onDelete when delete item is clicked", () => {
    const onDelete = vi.fn();
    renderMenu({
      canEdit: true,
      canDeleteSelf: true,
      canModDelete: false,
      onDelete,
    });
    fireEvent.click(screen.getByTestId("message-actions-trigger"));
    fireEvent.click(screen.getByTestId("message-actions-delete"));
    expect(onDelete).toHaveBeenCalledOnce();
  });
});
