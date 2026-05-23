import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, waitFor, fireEvent } from "@testing-library/react";
import { MemoryRouter, Routes, Route } from "react-router-dom";
import { I18nextProvider } from "react-i18next";
import { i18n } from "../../src/lib/i18n";
import { GuildsPage } from "../../src/app/GuildsPage";

function renderPage() {
  return render(
    <I18nextProvider i18n={i18n}>
      <MemoryRouter initialEntries={["/t/acme/guilds"]}>
        <Routes>
          <Route path="/t/:slug/guilds" element={<GuildsPage />} />
        </Routes>
      </MemoryRouter>
    </I18nextProvider>,
  );
}

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
});
