/**
 * SendBox unit tests (Plan 1.0 Task 13)
 */
import { describe, it, expect, vi } from "vitest";
import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { I18nextProvider } from "react-i18next";
import { i18n } from "../../src/lib/i18n";
import { SendBox } from "../../src/app/messaging/SendBox";

function renderBox(
  onSend: (content: string) => Promise<void>,
  disabled = false,
) {
  void i18n.changeLanguage("en");
  return render(
    <I18nextProvider i18n={i18n}>
      <SendBox onSend={onSend} disabled={disabled} />
    </I18nextProvider>,
  );
}

describe("SendBox", () => {
  it("Enter sends trimmed content; Shift+Enter inserts newline without sending", async () => {
    const onSend = vi.fn().mockResolvedValue(undefined);
    renderBox(onSend);

    const input = screen.getByTestId("send-box-input");

    // Shift+Enter should NOT call onSend
    fireEvent.change(input, { target: { value: "hello" } });
    fireEvent.keyDown(input, { key: "Enter", shiftKey: true });
    expect(onSend).not.toHaveBeenCalled();

    // Plain Enter SHOULD call onSend with trimmed value
    fireEvent.keyDown(input, { key: "Enter", shiftKey: false });

    await waitFor(() => {
      expect(onSend).toHaveBeenCalledTimes(1);
      expect(onSend).toHaveBeenCalledWith("hello");
    });
  });

  it("empty and whitespace-only inputs do NOT trigger onSend", async () => {
    const onSend = vi.fn().mockResolvedValue(undefined);
    renderBox(onSend);

    const input = screen.getByTestId("send-box-input");

    // empty
    fireEvent.change(input, { target: { value: "" } });
    fireEvent.keyDown(input, { key: "Enter", shiftKey: false });
    expect(onSend).not.toHaveBeenCalled();

    // whitespace only
    fireEvent.change(input, { target: { value: "   " } });
    fireEvent.keyDown(input, { key: "Enter", shiftKey: false });
    expect(onSend).not.toHaveBeenCalled();
  });

  it("double-click send button fires onSend exactly once (isSendingRef guard)", async () => {
    // onSend resolves after we assert, simulating a slow network
    let resolve!: () => void;
    const pendingPromise = new Promise<void>((r) => {
      resolve = r;
    });
    const onSend = vi.fn().mockReturnValue(pendingPromise);
    renderBox(onSend);

    const input = screen.getByTestId("send-box-input");
    const button = screen.getByTestId("send-box-button");

    fireEvent.change(input, { target: { value: "hi" } });

    // First click
    fireEvent.click(button);
    // Second click while first is still in-flight (input was cleared, button should be disabled)
    fireEvent.click(button);

    // Resolve the pending promise
    resolve();

    await waitFor(() => {
      expect(onSend).toHaveBeenCalledTimes(1);
    });
  });
});
