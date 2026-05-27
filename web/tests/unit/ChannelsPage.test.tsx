/**
 * ChannelsPage unit tests (Plan 1.0 Task 12)
 *
 * Migrated to typed openapi-fetch + TanStack Query (PR 5c).
 *
 * SignalR is mocked to prevent real WebSocket connections from being opened.
 * Fetch is stubbed globally per test via real Response objects (required so
 * openapi-fetch middleware's `instanceof Response` check passes).
 */
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
import { ChannelsPage } from "../../src/app/messaging/ChannelsPage";
import { createApiClient } from "../../src/lib/api/client";

// ---------------------------------------------------------------------------
// Mock useApiClient — absolute baseUrl required for jsdom URL construction.
// All fetch mocks must return real Response objects because the auth middleware
// uses `instanceof Response` in its onResponse handler.
// ---------------------------------------------------------------------------
vi.mock("@/lib/api/useApiClient", () => ({
  useApiClient: () =>
    createApiClient(
      () => null,
      () => Promise.resolve(null),
      "http://localhost/",
    ),
}));

// ---------------------------------------------------------------------------
// Mock @microsoft/signalr so no WebSocket is opened in jsdom.
// ---------------------------------------------------------------------------
vi.mock("@microsoft/signalr", () => {
  const HubConnectionState = {
    Disconnected: "Disconnected",
    Connecting: "Connecting",
    Connected: "Connected",
    Reconnecting: "Reconnecting",
    Disconnecting: "Disconnecting",
  } as const;

  const fakeConn = {
    on: vi.fn(),
    onreconnecting: vi.fn(),
    onreconnected: vi.fn(),
    onclose: vi.fn(),
    start: () => Promise.resolve(),
    stop: () => Promise.resolve(),
  };

  class HubConnectionBuilder {
    withUrl() {
      return this;
    }
    withAutomaticReconnect() {
      return this;
    }
    configureLogging() {
      return this;
    }
    build() {
      return fakeConn;
    }
  }

  return { HubConnectionBuilder, HubConnectionState, LogLevel: { Warning: 1 } };
});

// ---------------------------------------------------------------------------
// Helpers
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

// ---------------------------------------------------------------------------
// Shared fixtures
// ---------------------------------------------------------------------------

const fakeAuth = {
  state: {
    kind: "authenticated" as const,
    accessToken: "test-token",
    user: { id: 1, email: "o@acme.test", displayName: "Owner", role: "Owner" },
    tenant: { id: 1, slug: "acme", name: "ACME" },
  },
  login: vi.fn(),
  logout: vi.fn(),
  refreshAccessToken: vi.fn(),
};

const GUILD_ID = "aaaa0000-0000-0000-0000-000000000001";
const CH_TEXT = "bbbb0000-0000-0000-0000-000000000001";
const CH_TEXT2 = "bbbb0000-0000-0000-0000-000000000002";
const CH_VOICE = "cccc0000-0000-0000-0000-000000000001";

const baseChannels = [
  {
    publicId: CH_TEXT,
    discordChannelId: "111111111111111111",
    name: "general",
    channelType: 0,
    isBridged: false,
  },
  {
    publicId: CH_TEXT2,
    discordChannelId: "222222222222222222",
    name: "announcements",
    channelType: 0,
    isBridged: true,
  },
  {
    publicId: CH_VOICE,
    discordChannelId: "333333333333333333",
    name: "voice-lobby",
    channelType: 2,
    isBridged: false,
  },
];

function makeQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });
}

function renderPage(authOverride = fakeAuth, lang = "en") {
  void i18n.changeLanguage(lang);
  const queryClient = makeQueryClient();
  return render(
    <QueryClientProvider client={queryClient}>
      <AuthContext.Provider value={authOverride}>
        <I18nextProvider i18n={i18n}>
          <MemoryRouter
            initialEntries={[`/t/acme/guilds/${GUILD_ID}/channels`]}
          >
            <Routes>
              <Route
                path="/t/:slug/guilds/:guildPublicId/channels"
                element={<ChannelsPage />}
              />
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

describe("ChannelsPage", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("renders list of channels with bridge checkboxes", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(jsonResponse({ channels: baseChannels })),
    );

    renderPage();

    await waitFor(() => {
      expect(
        screen.getByTestId(`channel-bridge-toggle-${CH_TEXT}`),
      ).toBeInTheDocument();
      expect(
        screen.getByTestId(`channel-bridge-toggle-${CH_TEXT2}`),
      ).toBeInTheDocument();
      expect(
        screen.getByTestId(`channel-bridge-toggle-${CH_VOICE}`),
      ).toBeInTheDocument();
    });

    // text channels have unchecked / checked state
    expect(
      screen.getByTestId(`channel-bridge-toggle-${CH_TEXT}`),
    ).not.toBeChecked();
    expect(
      screen.getByTestId(`channel-bridge-toggle-${CH_TEXT2}`),
    ).toBeChecked();
  });

  it("voice channels are rendered disabled with 'not bridgeable' label", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(jsonResponse({ channels: baseChannels })),
    );

    renderPage();

    await waitFor(() => {
      const voiceCheckbox = screen.getByTestId(
        `channel-bridge-toggle-${CH_VOICE}`,
      );
      expect(voiceCheckbox).toBeDisabled();
    });

    expect(
      screen.getByTestId(`channel-not-bridgeable-${CH_VOICE}`),
    ).toBeInTheDocument();
  });

  it("bridge toggle calls POST with typed client", async () => {
    const fetchMock = vi
      .fn()
      .mockImplementation(async (input: RequestInfo | URL) => {
        const req = input instanceof Request ? input : null;
        const url = req ? req.url : String(input);
        const method = req ? req.method : "GET";
        if (method === "POST" && url.includes("/bridge")) {
          return jsonResponse({ backfillJobId: 1 }, 202);
        }
        if (method === "POST" && url.includes("/sync")) {
          return emptyResponse(204);
        }
        return jsonResponse({ channels: baseChannels });
      });
    vi.stubGlobal("fetch", fetchMock);

    renderPage();

    await waitFor(() => {
      expect(
        screen.getByTestId(`channel-bridge-toggle-${CH_TEXT}`),
      ).toBeInTheDocument();
    });

    await act(async () => {
      fireEvent.click(screen.getByTestId(`channel-bridge-toggle-${CH_TEXT}`));
    });

    await waitFor(() => {
      const postCall = fetchMock.mock.calls.find((args: unknown[]) => {
        const req = args[0] instanceof Request ? args[0] : null;
        const url = req ? req.url : String(args[0]);
        const method = req ? req.method : (args[1] as RequestInit)?.method;
        return method === "POST" && url.includes(`/channels/${CH_TEXT}/bridge`);
      });
      expect(postCall).toBeDefined();
    });
  });

  it("bridge toggle reverts checkbox state on API 409 error", async () => {
    const fetchMock = vi
      .fn()
      .mockImplementation(async (input: RequestInfo | URL) => {
        const req = input instanceof Request ? input : null;
        const url = req ? req.url : String(input);
        const method = req ? req.method : "GET";
        if (method === "POST" && url.includes("/bridge")) {
          return jsonResponse({}, 409);
        }
        return jsonResponse({ channels: baseChannels });
      });
    vi.stubGlobal("fetch", fetchMock);

    renderPage();

    await waitFor(() => {
      expect(
        screen.getByTestId(`channel-bridge-toggle-${CH_TEXT}`),
      ).not.toBeChecked();
    });

    await act(async () => {
      fireEvent.click(screen.getByTestId(`channel-bridge-toggle-${CH_TEXT}`));
    });

    // After optimistic update it may be checked briefly, then reverted
    await waitFor(() => {
      expect(
        screen.getByTestId(`channel-bridge-toggle-${CH_TEXT}`),
      ).not.toBeChecked();
    });
  });

  it("shows backfill progress text when status is running", async () => {
    const channelsWithBackfill = [
      {
        ...baseChannels[0],
        backfill: { status: "running", fetchedCount: 42 },
      },
    ];
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValue(jsonResponse({ channels: channelsWithBackfill })),
    );

    renderPage();

    await waitFor(() => {
      expect(
        screen.getByTestId(`channel-backfill-progress-${CH_TEXT}`),
      ).toBeInTheDocument();
    });

    expect(
      screen.getByTestId(`channel-backfill-progress-${CH_TEXT}`).textContent,
    ).toMatch(/42/);
  });

  it("shows error fallback when API returns 500", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(jsonResponse({}, 500)));

    renderPage();

    await waitFor(() => {
      expect(screen.getByTestId("channels-error")).toBeInTheDocument();
    });
  });

  it("renders localized labels in English", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(jsonResponse({ channels: baseChannels })),
    );

    renderPage(fakeAuth, "en");

    await waitFor(() => {
      expect(screen.getByRole("heading", { level: 1 })).toHaveTextContent(
        "Channels",
      );
    });
  });

  it("renders localized labels in German", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(jsonResponse({ channels: baseChannels })),
    );

    renderPage(fakeAuth, "de");

    await waitFor(() => {
      expect(screen.getByRole("heading", { level: 1 })).toHaveTextContent(
        "Kanäle",
      );
    });
  });
});
