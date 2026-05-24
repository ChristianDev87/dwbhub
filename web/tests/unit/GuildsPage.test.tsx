import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, waitFor, fireEvent } from "@testing-library/react";
import { MemoryRouter, Routes, Route } from "react-router-dom";
import { I18nextProvider } from "react-i18next";
import { i18n } from "../../src/lib/i18n";
import { AuthContext } from "../../src/app/auth-context";
import { GuildsPage } from "../../src/app/GuildsPage";

const fakeAuthValue = {
  state: {
    kind: "authenticated" as const,
    accessToken: "test-access-token",
    user: { id: 1, email: "owner@acme.test", displayName: "Owner", role: "Owner" },
    tenant: { id: 1, slug: "acme", name: "ACME" },
  },
  login: vi.fn(),
  logout: vi.fn(),
  refresh: vi.fn(),
};

function renderPage() {
  return render(
    <AuthContext.Provider value={fakeAuthValue}>
      <I18nextProvider i18n={i18n}>
        <MemoryRouter initialEntries={["/t/acme/guilds"]}>
          <Routes>
            <Route path="/t/:slug/guilds" element={<GuildsPage />} />
          </Routes>
        </MemoryRouter>
      </I18nextProvider>
    </AuthContext.Provider>,
  );
}

const baseGuild = {
  publicId: "11111111-1111-1111-1111-111111111111",
  discordGuildId: "1234567890123456789",
  displayName: "Production",
  isActive: true,
  registeredAt: "2026-05-22T00:00:00Z",
  botCredentialsConfigured: true,
  botConnectionState: "connected",
};

describe("GuildsPage", () => {
  beforeEach(() => {
    vi.restoreAllMocks();
  });

  it("renders empty state when API returns no guilds", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue({
        ok: true,
        status: 200,
        json: async () => ({ guilds: [] }),
      }),
    );
    renderPage();
    await waitFor(() => {
      expect(screen.getByTestId("guilds-empty")).toBeInTheDocument();
    });
  });

  it("validates discord guild id format", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue({
        ok: true,
        status: 200,
        json: async () => ({ guilds: [] }),
      }),
    );
    renderPage();
    await waitFor(() => screen.getByTestId("input-discord-guild-id"));
    fireEvent.change(screen.getByTestId("input-discord-guild-id"), {
      target: { value: "abc" },
    });
    fireEvent.change(screen.getByTestId("input-guild-display-name"), {
      target: { value: "Test" },
    });
    fireEvent.click(screen.getByTestId("add-guild-submit"));
    await waitFor(() => {
      expect(screen.getByTestId("error-discord-guild-id")).toBeInTheDocument();
    });
  });

  it("validates display name is non-empty", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue({
        ok: true,
        status: 200,
        json: async () => ({ guilds: [] }),
      }),
    );
    renderPage();
    await waitFor(() => screen.getByTestId("input-discord-guild-id"));
    fireEvent.change(screen.getByTestId("input-discord-guild-id"), {
      target: { value: "1234567890123456789" },
    });
    fireEvent.change(screen.getByTestId("input-guild-display-name"), {
      target: { value: "" },
    });
    fireEvent.click(screen.getByTestId("add-guild-submit"));
    await waitFor(() => {
      expect(
        screen.getByTestId("error-guild-display-name"),
      ).toBeInTheDocument();
    });
  });

  it("calls POST on add and refreshes list", async () => {
    let callCount = 0;
    const fetchMock = vi
      .fn()
      .mockImplementation(async (_url: string, init?: RequestInit) => {
        callCount += 1;
        if (init?.method === "POST") {
          return {
            ok: true,
            status: 201,
            json: async () => ({
              publicId: "11111111-1111-1111-1111-111111111111",
              discordGuildId: "1234567890123456789",
              displayName: "Production",
              isActive: true,
              registeredAt: "2026-05-22T00:00:00Z",
              botCredentialsConfigured: false,
              botConnectionState: null,
            }),
          };
        }
        if (callCount === 1) {
          return { ok: true, status: 200, json: async () => ({ guilds: [] }) };
        }
        return {
          ok: true,
          status: 200,
          json: async () => ({
            guilds: [
              {
                publicId: "11111111-1111-1111-1111-111111111111",
                discordGuildId: "1234567890123456789",
                displayName: "Production",
                isActive: true,
                registeredAt: "2026-05-22T00:00:00Z",
                botCredentialsConfigured: false,
                botConnectionState: null,
              },
            ],
          }),
        };
      });
    vi.stubGlobal("fetch", fetchMock);

    renderPage();
    await waitFor(() => screen.getByTestId("input-discord-guild-id"));
    fireEvent.change(screen.getByTestId("input-discord-guild-id"), {
      target: { value: "1234567890123456789" },
    });
    fireEvent.change(screen.getByTestId("input-guild-display-name"), {
      target: { value: "Production" },
    });
    fireEvent.click(screen.getByTestId("add-guild-submit"));

    await waitFor(() => {
      expect(screen.getByText("Production")).toBeInTheDocument();
    });
  });

  it("surfaces 409 already-registered error", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockImplementation(async (_url: string, init?: RequestInit) => {
        if (init?.method === "POST") {
          return {
            ok: false,
            status: 409,
            json: async () => ({ error: "guild_already_registered" }),
          };
        }
        return { ok: true, status: 200, json: async () => ({ guilds: [] }) };
      }),
    );

    renderPage();
    await waitFor(() => screen.getByTestId("input-discord-guild-id"));
    fireEvent.change(screen.getByTestId("input-discord-guild-id"), {
      target: { value: "1234567890123456789" },
    });
    fireEvent.change(screen.getByTestId("input-guild-display-name"), {
      target: { value: "Dup" },
    });
    fireEvent.click(screen.getByTestId("add-guild-submit"));
    await waitFor(() => {
      expect(screen.getByTestId("error-add-guild")).toBeInTheDocument();
    });
  });

  it("opens confirm modal on delete and calls DELETE on confirm", async () => {
    let phase: "initial" | "afterDelete" = "initial";
    const fetchMock = vi
      .fn()
      .mockImplementation(async (_url: string, init?: RequestInit) => {
        if (init?.method === "DELETE") {
          phase = "afterDelete";
          return { ok: true, status: 204, json: async () => ({}) };
        }
        if (phase === "initial") {
          return {
            ok: true,
            status: 200,
            json: async () => ({
              guilds: [
                {
                  publicId: "22222222-2222-2222-2222-222222222222",
                  discordGuildId: "9876543210987654321",
                  displayName: "Staging",
                  isActive: true,
                  registeredAt: "2026-05-22T00:00:00Z",
                  botCredentialsConfigured: false,
                  botConnectionState: null,
                },
              ],
            }),
          };
        }
        return { ok: true, status: 200, json: async () => ({ guilds: [] }) };
      });
    vi.stubGlobal("fetch", fetchMock);

    renderPage();
    await waitFor(() => screen.getByText("Staging"));
    fireEvent.click(screen.getByTestId("delete-guild-button"));
    await waitFor(() => screen.getByTestId("delete-confirm-yes"));
    fireEvent.click(screen.getByTestId("delete-confirm-yes"));
    await waitFor(() => {
      expect(screen.queryByText("Staging")).not.toBeInTheDocument();
    });
  });

  // --- Plan 0.8 Task 10: bot-connection status + pause/resume/reconnect ---

  it("renders a green status indicator for connected guild", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue({
        ok: true,
        status: 200,
        json: async () => ({
          guilds: [{ ...baseGuild, botConnectionState: "connected" }],
        }),
      }),
    );
    renderPage();
    await waitFor(() => {
      const indicator = screen.getByTestId(
        `guild-status-${baseGuild.publicId}`,
      );
      expect(indicator).toBeInTheDocument();
      expect(indicator).toHaveClass("text-green-600");
    });
  });

  it("renders an amber indicator and starts polling when guild is connecting", async () => {
    // Use fake timers with shouldAdvanceTime so waitFor's internal polling works
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        guilds: [{ ...baseGuild, botConnectionState: "connecting" }],
      }),
    });
    vi.stubGlobal("fetch", fetchMock);

    renderPage();

    // Wait for the amber indicator
    await waitFor(() => {
      const indicator = screen.getByTestId(
        `guild-status-${baseGuild.publicId}`,
      );
      expect(indicator).toBeInTheDocument();
      expect(indicator).toHaveClass("text-amber-500");
    });

    const callsBefore = fetchMock.mock.calls.length;

    // Advance fake time past the 3s polling interval
    vi.advanceTimersByTime(3100);

    await waitFor(() => {
      expect(fetchMock.mock.calls.length).toBeGreaterThan(callsBefore);
    });

    vi.useRealTimers();
  });

  it("opens pause modal when Pause button is clicked", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue({
        ok: true,
        status: 200,
        json: async () => ({
          guilds: [
            { ...baseGuild, isActive: true, botConnectionState: "connected" },
          ],
        }),
      }),
    );
    renderPage();
    await waitFor(() =>
      screen.getByTestId(`guild-pause-${baseGuild.publicId}`),
    );
    fireEvent.click(screen.getByTestId(`guild-pause-${baseGuild.publicId}`));
    await waitFor(() => {
      expect(screen.getByTestId("pause-guild-modal")).toBeInTheDocument();
    });
  });

  it("calls deactivate on confirm and closes modal", async () => {
    const fetchMock = vi
      .fn()
      .mockImplementation(async (url: string, init?: RequestInit) => {
        if (init?.method === "POST" && String(url).includes("/deactivate")) {
          return { ok: true, status: 204, json: async () => ({}) };
        }
        return {
          ok: true,
          status: 200,
          json: async () => ({
            guilds: [
              { ...baseGuild, isActive: true, botConnectionState: "connected" },
            ],
          }),
        };
      });
    vi.stubGlobal("fetch", fetchMock);

    renderPage();
    await waitFor(() =>
      screen.getByTestId(`guild-pause-${baseGuild.publicId}`),
    );
    fireEvent.click(screen.getByTestId(`guild-pause-${baseGuild.publicId}`));
    await waitFor(() => screen.getByTestId("pause-modal-confirm"));
    fireEvent.click(screen.getByTestId("pause-modal-confirm"));
    await waitFor(() => {
      expect(
        fetchMock.mock.calls.some(
          (args: unknown[]) =>
            String(args[0]).includes("/deactivate") &&
            (args[1] as RequestInit)?.method === "POST",
        ),
      ).toBe(true);
    });
    await waitFor(() => {
      expect(screen.queryByTestId("pause-guild-modal")).not.toBeInTheDocument();
    });
  });

  it("shows Resume button (not Pause) when guild is inactive", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue({
        ok: true,
        status: 200,
        json: async () => ({
          guilds: [
            {
              ...baseGuild,
              isActive: false,
              botConnectionState: "disconnected",
            },
          ],
        }),
      }),
    );
    renderPage();
    await waitFor(() => {
      expect(
        screen.getByTestId(`guild-resume-${baseGuild.publicId}`),
      ).toBeInTheDocument();
      expect(
        screen.queryByTestId(`guild-pause-${baseGuild.publicId}`),
      ).not.toBeInTheDocument();
    });
  });
});
