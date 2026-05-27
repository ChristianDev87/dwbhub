import { describe, it, expect, vi, beforeEach } from "vitest";
import {
  render,
  screen,
  waitFor,
  fireEvent,
  act,
} from "@testing-library/react";
import { MemoryRouter, Routes, Route } from "react-router-dom";
import { I18nextProvider } from "react-i18next";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { i18n } from "../../src/lib/i18n";
import { AuthContext } from "../../src/app/auth-context";
import { GuildsPage } from "../../src/app/GuildsPage";
import { createApiClient } from "../../src/lib/api/client";

/**
 * openapi-fetch internally calls `new URL(path, baseUrl)`. In the jsdom test
 * environment baseUrl: "/" is not a valid absolute URL and throws TypeError.
 * We mock useApiClient to supply a client with an absolute localhost baseUrl
 * so URL construction succeeds, while globalThis.fetch is stubbed separately
 * to intercept calls without hitting the network.
 *
 * The auth middleware's onResponse() returns `response` only on non-401 status;
 * openapi-fetch checks `instanceof Response`. Therefore all fetch mocks must
 * return real Response objects (not plain objects).
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
// Helpers to build real Response objects that satisfy openapi-fetch's
// `instanceof Response` check in the middleware loop.
// ---------------------------------------------------------------------------
function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

function emptyResponse(status = 204): Response {
  return new Response(null, { status });
}

const fakeAuthValue = {
  state: {
    kind: "authenticated" as const,
    accessToken: "test-access-token",
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
  refreshAccessToken: vi.fn(),
};

function makeQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });
}

function renderPage() {
  const queryClient = makeQueryClient();
  return render(
    <QueryClientProvider client={queryClient}>
      <AuthContext.Provider value={fakeAuthValue}>
        <I18nextProvider i18n={i18n}>
          <MemoryRouter initialEntries={["/t/acme/guilds"]}>
            <Routes>
              <Route path="/t/:slug/guilds" element={<GuildsPage />} />
            </Routes>
          </MemoryRouter>
        </I18nextProvider>
      </AuthContext.Provider>
    </QueryClientProvider>,
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
      vi.fn().mockResolvedValue(jsonResponse({ guilds: [] })),
    );
    renderPage();
    await waitFor(() => {
      expect(screen.getByTestId("guilds-empty")).toBeInTheDocument();
    });
  });

  it("validates discord guild id format", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(jsonResponse({ guilds: [] })),
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
      vi.fn().mockResolvedValue(jsonResponse({ guilds: [] })),
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
      .mockImplementation(
        async (input: RequestInfo | URL, init?: RequestInit) => {
          const method =
            init?.method ?? (input instanceof Request ? input.method : "GET");
          callCount += 1;
          if (method === "POST") {
            return jsonResponse(
              {
                publicId: "11111111-1111-1111-1111-111111111111",
                discordGuildId: "1234567890123456789",
                displayName: "Production",
                isActive: true,
                registeredAt: "2026-05-22T00:00:00Z",
                botCredentialsConfigured: false,
                botConnectionState: null,
              },
              201,
            );
          }
          if (callCount === 1) {
            return jsonResponse({ guilds: [] });
          }
          return jsonResponse({
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
          });
        },
      );
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
      vi
        .fn()
        .mockImplementation(
          async (input: RequestInfo | URL, init?: RequestInit) => {
            const method =
              init?.method ?? (input instanceof Request ? input.method : "GET");
            if (method === "POST") {
              return jsonResponse({ error: "guild_already_registered" }, 409);
            }
            return jsonResponse({ guilds: [] });
          },
        ),
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
      .mockImplementation(
        async (input: RequestInfo | URL, init?: RequestInit) => {
          const method =
            init?.method ?? (input instanceof Request ? input.method : "GET");
          if (method === "DELETE") {
            phase = "afterDelete";
            return emptyResponse(204);
          }
          if (phase === "initial") {
            return jsonResponse({
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
            });
          }
          return jsonResponse({ guilds: [] });
        },
      );
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
      vi.fn().mockResolvedValue(
        jsonResponse({
          guilds: [{ ...baseGuild, botConnectionState: "connected" }],
        }),
      ),
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
    const fetchMock = vi.fn().mockResolvedValue(
      jsonResponse({
        guilds: [{ ...baseGuild, botConnectionState: "connecting" }],
      }),
    );
    vi.stubGlobal("fetch", fetchMock);

    renderPage();

    // Wait for the amber indicator — confirms the initial fetch resolved and
    // the guilds state was committed to the DOM.
    await waitFor(() => {
      const indicator = screen.getByTestId(
        `guild-status-${baseGuild.publicId}`,
      );
      expect(indicator).toBeInTheDocument();
      expect(indicator).toHaveClass("text-amber-500");
    });

    // Flush any pending React effects so TanStack Query's refetchInterval
    // timer is registered before we advance fake time.
    await act(async () => {
      await Promise.resolve();
    });

    const callsBefore = fetchMock.mock.calls.length;

    // Advance fake time past the 3 s polling interval and let async callbacks
    // settle (runOnlyPendingTimersAsync drains timer callbacks + their
    // microtasks, unlike the fire-and-forget advanceTimersByTime).
    await act(async () => {
      await vi.runOnlyPendingTimersAsync();
    });

    expect(fetchMock.mock.calls.length).toBeGreaterThan(callsBefore);

    vi.useRealTimers();
  });

  it("opens pause modal when Pause button is clicked", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(
        jsonResponse({
          guilds: [
            { ...baseGuild, isActive: true, botConnectionState: "connected" },
          ],
        }),
      ),
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
      .mockImplementation(
        async (input: RequestInfo | URL, init?: RequestInit) => {
          const method =
            init?.method ?? (input instanceof Request ? input.method : "GET");
          const url = input instanceof Request ? input.url : String(input);
          if (method === "POST" && url.includes("/deactivate")) {
            return emptyResponse(204);
          }
          return jsonResponse({
            guilds: [
              {
                ...baseGuild,
                isActive: true,
                botConnectionState: "connected",
              },
            ],
          });
        },
      );
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
        fetchMock.mock.calls.some((args: unknown[]) => {
          const input = args[0] as RequestInfo | URL;
          const init = args[1] as RequestInit | undefined;
          const method =
            init?.method ?? (input instanceof Request ? input.method : "GET");
          const url = input instanceof Request ? input.url : String(input);
          return url.includes("/deactivate") && method === "POST";
        }),
      ).toBe(true);
    });
    await waitFor(() => {
      expect(screen.queryByTestId("pause-guild-modal")).not.toBeInTheDocument();
    });
  });

  it("shows Resume button (not Pause) when guild is inactive", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(
        jsonResponse({
          guilds: [
            {
              ...baseGuild,
              isActive: false,
              botConnectionState: "disconnected",
            },
          ],
        }),
      ),
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
