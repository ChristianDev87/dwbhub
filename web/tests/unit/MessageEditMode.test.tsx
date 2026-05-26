/**
 * MessageEditMode unit tests (Plan 1.1 Task 10)
 */

import { describe, it, expect, vi } from "vitest";
import { render, screen, fireEvent } from "@testing-library/react";
import { I18nextProvider } from "react-i18next";
import { i18n } from "../../src/lib/i18n";
import { MessageEditMode } from "../../src/app/messaging/MessageEditMode";

function renderEditMode(props: {
  initialContent?: string;
  onSave?: (c: string) => void;
  onCancel?: () => void;
  saving?: boolean;
}) {
  void i18n.changeLanguage("en");
  return render(
    <I18nextProvider i18n={i18n}>
      <MessageEditMode
        initialContent={props.initialContent ?? "hello"}
        onSave={props.onSave ?? vi.fn()}
        onCancel={props.onCancel ?? vi.fn()}
        {...(props.saving !== undefined ? { saving: props.saving } : {})}
      />
    </I18nextProvider>,
  );
}

describe("MessageEditMode", () => {
  it("renders textarea with initial content", () => {
    renderEditMode({ initialContent: "my message" });
    const textarea = screen.getByTestId(
      "message-edit-textarea",
    ) as HTMLTextAreaElement;
    expect(textarea.value).toBe("my message");
  });

  it("shows char count reflecting current input length", () => {
    renderEditMode({ initialContent: "hello" });
    expect(screen.getByTestId("message-edit-char-count")).toHaveTextContent(
      "5 / 2000",
    );
  });

  it("disables save button when content is unchanged", () => {
    renderEditMode({ initialContent: "hello" });
    const saveBtn = screen.getByTestId("message-edit-save");
    expect(saveBtn).toBeDisabled();
  });

  it("disables save button when content is empty", () => {
    renderEditMode({ initialContent: "hello" });
    const textarea = screen.getByTestId("message-edit-textarea");
    fireEvent.change(textarea, { target: { value: "   " } });
    expect(screen.getByTestId("message-edit-save")).toBeDisabled();
  });

  it("enables save button when content is changed and non-empty", () => {
    renderEditMode({ initialContent: "hello" });
    const textarea = screen.getByTestId("message-edit-textarea");
    fireEvent.change(textarea, { target: { value: "hello world" } });
    expect(screen.getByTestId("message-edit-save")).not.toBeDisabled();
  });

  it("calls onSave with trimmed content on Enter key", () => {
    const onSave = vi.fn();
    renderEditMode({ initialContent: "hello", onSave });
    const textarea = screen.getByTestId("message-edit-textarea");
    fireEvent.change(textarea, { target: { value: "  new content  " } });
    fireEvent.keyDown(textarea, { key: "Enter", shiftKey: false });
    expect(onSave).toHaveBeenCalledWith("new content");
  });

  it("does NOT call onSave on Shift+Enter", () => {
    const onSave = vi.fn();
    renderEditMode({ initialContent: "hello", onSave });
    const textarea = screen.getByTestId("message-edit-textarea");
    fireEvent.change(textarea, { target: { value: "new content" } });
    fireEvent.keyDown(textarea, { key: "Enter", shiftKey: true });
    expect(onSave).not.toHaveBeenCalled();
  });

  it("calls onCancel on Escape key", () => {
    const onCancel = vi.fn();
    renderEditMode({ onCancel });
    const textarea = screen.getByTestId("message-edit-textarea");
    fireEvent.keyDown(textarea, { key: "Escape" });
    expect(onCancel).toHaveBeenCalledOnce();
  });

  it("calls onCancel when cancel button is clicked", () => {
    const onCancel = vi.fn();
    renderEditMode({ onCancel });
    fireEvent.click(screen.getByTestId("message-edit-cancel"));
    expect(onCancel).toHaveBeenCalledOnce();
  });

  it("disables textarea and save while saving=true", () => {
    renderEditMode({ initialContent: "hello", saving: true });
    expect(screen.getByTestId("message-edit-textarea")).toBeDisabled();
    expect(screen.getByTestId("message-edit-save")).toBeDisabled();
  });
});
