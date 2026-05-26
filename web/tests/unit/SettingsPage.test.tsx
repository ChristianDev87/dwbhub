/**
 * SettingsPage unit tests (Plan 1.1 Task 11)
 *
 * 4 cases:
 *   1. Renders dropdown with current tenant value (default = Unlimited)
 *   2. Submits new window seconds on save
 *   3. Rejects out-of-range custom value with validation message
 *   4. Hides Owner-gated form for non-Owner users (shows access-denied)
 */

import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { MemoryRouter, Routes, Route } from "react-router-dom";
import { I18nextProvider } from "react-i18next";
import { i18n } from "../../src/lib/i18n";
import { AuthContext } from "../../src/app/auth-context";
import { SettingsPage } from "../../src/app/SettingsPage";

// ---------------------------------------------------------------------------
// Auth fixtures
// ---------------------------------------------------------------------------

function makeOwnerAuth() {
  return {
    state: {
      kind: "authenticated" as const,
      accessToken: "test-token",
      user: {
        id: 1,
        email: "owner@acme.test",
        displayName: "Owner",
        role: "Owner",
      },
      tenant: { id: 1, slug: "acme", name: "ACME" },
    },
    login: vi.fn(),
    logout: vi.fn(),
    refresh: vi.fn(),
  };
}

function makeMemberAuth() {
  return {
    state: {
      kind: "authenticated" as const,
      accessToken: "test-token",
      user: {
        id: 2,
        email: "member@acme.test",
        displayName: "Member",
        role: "Member",
      },
      tenant: { id: 1, slug: "acme", name: "ACME" },
    },
    login: vi.fn(),
    logout: vi.fn(),
    refresh: vi.fn(),
  };
}

// ---------------------------------------------------------------------------
// Render helper
// ---------------------------------------------------------------------------

function renderSettings(auth = makeOwnerAuth()) {
  void i18n.changeLanguage("en");
  return render(
    <AuthContext.Provider value={auth}>
      <I18nextProvider i18n={i18n}>
        <MemoryRouter initialEntries={["/t/acme/settings"]}>
          <Routes>
            <Route path="/t/:slug/settings" element={<SettingsPage />} />
            <Route path="/t/:slug/dashboard" element={<div>Dashboard</div>} />
          </Routes>
        </MemoryRouter>
      </I18nextProvider>
    </AuthContext.Provider>,
  );
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe("SettingsPage", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("renders dropdown with current tenant value (default Unlimited)", () => {
    renderSettings();

    // The page heading renders
    expect(screen.getByTestId("settings-title")).toBeInTheDocument();

    // Dropdown is present
    const select = screen.getByTestId("settings-edit-window-select");
    expect(select).toBeInTheDocument();

    // Default selected option is Unlimited (value="null")
    expect((select as HTMLSelectElement).value).toBe("null");
  });

  it("submits new window seconds on save", async () => {
    let patchBody: Record<string, unknown> | null = null;
    vi.stubGlobal(
      "fetch",
      vi.fn().mockImplementation(async (url: string, init?: RequestInit) => {
        if (
          typeof url === "string" &&
          url.includes("/settings") &&
          init?.method === "PATCH"
        ) {
          patchBody = JSON.parse(init.body as string) as Record<
            string,
            unknown
          >;
          return { ok: true, status: 204, json: async () => ({}) };
        }
        return { ok: true, status: 200, json: async () => ({}) };
      }),
    );

    renderSettings();

    // Select "1 Hour" (value=3600)
    const select = screen.getByTestId("settings-edit-window-select");
    fireEvent.change(select, { target: { value: "3600" } });
    expect((select as HTMLSelectElement).value).toBe("3600");

    // Submit
    const saveBtn = screen.getByTestId("settings-save-button");
    fireEvent.click(saveBtn);

    // Success message appears
    await waitFor(() => {
      expect(screen.getByTestId("settings-saved")).toBeInTheDocument();
    });

    // PATCH was called with correct body
    expect(patchBody).toEqual({ messageEditWindowSeconds: 3600 });
  });

  it("rejects out-of-range custom value with validation message", async () => {
    renderSettings();

    // Select Custom option
    const select = screen.getByTestId("settings-edit-window-select");
    fireEvent.change(select, { target: { value: "custom" } });

    // Custom input appears
    const customInput = screen.getByTestId("settings-custom-minutes");
    expect(customInput).toBeInTheDocument();

    // Enter invalid value (0 minutes — below minimum of 1)
    fireEvent.change(customInput, { target: { value: "0" } });

    // Submit
    const saveBtn = screen.getByTestId("settings-save-button");
    fireEvent.click(saveBtn);

    // Validation error shown — no fetch call
    await waitFor(() => {
      expect(screen.getByTestId("settings-custom-error")).toBeInTheDocument();
    });

    // Success message should NOT appear
    expect(screen.queryByTestId("settings-saved")).not.toBeInTheDocument();
  });

  it("hides for non-Owner users and shows access-denied message", () => {
    renderSettings(makeMemberAuth());

    // Access denied element visible
    expect(screen.getByTestId("settings-access-denied")).toBeInTheDocument();

    // Form NOT rendered
    expect(screen.queryByTestId("settings-form")).not.toBeInTheDocument();

    // Dropdown NOT rendered
    expect(
      screen.queryByTestId("settings-edit-window-select"),
    ).not.toBeInTheDocument();
  });
});
