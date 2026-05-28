import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, waitFor, fireEvent } from "@testing-library/react";
import { MemoryRouter, Routes, Route } from "react-router-dom";
import { I18nextProvider } from "react-i18next";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { i18n } from "../../src/lib/i18n";
import { AuthContext } from "../../src/app/auth-context";
import { SettingsPage } from "../../src/app/SettingsPage";
import { createApiClient } from "../../src/lib/api/client";

/**
 * openapi-fetch internally calls `new URL(path, baseUrl)`. In jsdom the
 * default baseUrl "/" is not a valid absolute URL. We mock useApiClient to
 * supply an absolute localhost baseUrl so URL construction succeeds, while
 * globalThis.fetch is stubbed to intercept calls.
 */
vi.mock("@/lib/api/useApiClient", () => ({
  useApiClient: () =>
    createApiClient(
      () => null,
      () => Promise.resolve(null),
      "http://localhost/",
    ),
}));

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function emptyResponse(status = 204): Response {
  return new Response(null, { status });
}

function makeQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });
}

interface RenderOptions {
  role?: "Owner" | "Member";
  messageEditWindowSeconds?: number | null;
}

function renderPage(opts: RenderOptions = {}) {
  const role = opts.role ?? "Owner";
  const queryClient = makeQueryClient();

  const fakeAuthValue = {
    state: {
      kind: "authenticated" as const,
      accessToken: "test-access-token",
      user: {
        id: 1,
        email: "user@acme.test",
        displayName: "User",
        role,
      },
      tenant: {
        id: 1,
        slug: "acme",
        name: "ACME",
        messageEditWindowSeconds: opts.messageEditWindowSeconds ?? null,
      },
    },
    login: vi.fn(),
    logout: vi.fn(),
    refreshAccessToken: vi.fn(),
  };

  return render(
    <QueryClientProvider client={queryClient}>
      <AuthContext.Provider value={fakeAuthValue}>
        <I18nextProvider i18n={i18n}>
          <MemoryRouter initialEntries={["/t/acme/settings"]}>
            <Routes>
              <Route path="/t/:slug/settings" element={<SettingsPage />} />
            </Routes>
          </MemoryRouter>
        </I18nextProvider>
      </AuthContext.Provider>
    </QueryClientProvider>,
  );
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe("SettingsPage", () => {
  beforeEach(() => {
    vi.restoreAllMocks();
  });

  it("renders_with_current_tenant_value", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(emptyResponse(204)));

    renderPage({ role: "Owner", messageEditWindowSeconds: 3600 });

    await waitFor(() => {
      const select = screen.getByRole("combobox", {
        name: /edit.fenster|edit window/i,
      });
      expect(select).toBeInTheDocument();
    });

    // The select should show the 1-hour preset because the tenant value = 3600.
    const select = screen.getByTestId("settings-edit-window-select");
    expect((select as HTMLSelectElement).value).toBe("3600");
  });

  it("saves_new_window_on_submit", async () => {
    const fetchMock = vi.fn().mockResolvedValue(emptyResponse(204));
    vi.stubGlobal("fetch", fetchMock);

    renderPage({ role: "Owner", messageEditWindowSeconds: null });

    // Change to 15-minute preset.
    const select = screen.getByTestId("settings-edit-window-select");
    fireEvent.change(select, { target: { value: "900" } });

    // Save button should now be enabled.
    const saveBtn = screen.getByTestId("settings-save-button");
    expect(saveBtn).not.toBeDisabled();

    fireEvent.click(saveBtn);

    await waitFor(() => {
      expect(fetchMock).toHaveBeenCalled();
      // openapi-fetch passes a Request object as first arg (no separate init).
      const calls = fetchMock.mock.calls as Array<
        [RequestInfo | URL, RequestInit?]
      >;
      const patchCall = calls.find(([input]) => {
        const method = input instanceof Request ? input.method : "GET";
        return method === "PATCH";
      });
      expect(patchCall).toBeDefined();
    });
  });

  it("rejects_out_of_range_custom_value", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(emptyResponse(204)));

    renderPage({ role: "Owner" });

    // Select custom preset.
    const select = screen.getByTestId("settings-edit-window-select");
    fireEvent.change(select, { target: { value: "custom" } });

    await waitFor(() => screen.getByTestId("settings-custom-minutes"));

    // Enter a value outside the allowed range.
    const input = screen.getByTestId("settings-custom-minutes");
    fireEvent.change(input, { target: { value: "0" } });

    await waitFor(() => {
      expect(screen.getByTestId("settings-custom-error")).toBeInTheDocument();
    });

    // Save button should be disabled due to invalid input.
    const saveBtn = screen.getByTestId("settings-save-button");
    expect(saveBtn).toBeDisabled();
  });

  it("hides_form_for_non_owner", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(emptyResponse(204)));

    renderPage({ role: "Member" });

    await waitFor(() => {
      expect(screen.getByTestId("settings-owner-only")).toBeInTheDocument();
    });

    expect(screen.queryByTestId("settings-edit-window-select")).toBeNull();
    expect(screen.queryByTestId("settings-save-button")).toBeNull();
  });
});
