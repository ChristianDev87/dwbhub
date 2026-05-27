/**
 * ChannelsPage unit tests (Plan 1.0 Task 12)
 *
 * SignalR is mocked to prevent real WebSocket connections from being opened.
 * Fetch is stubbed globally per test.
 */
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, waitFor, fireEvent } from "@testing-library/react";
import { MemoryRouter, Routes, Route } from "react-router-dom";
import { I18nextProvider } from "react-i18next";
import { i18n } from "../../src/lib/i18n";
import { AuthContext } from "../../src/app/auth-context";
import { ChannelsPage } from "../../src/app/messaging/ChannelsPage";

// ---------------------------------------------------------------------------
// Mock @microsoft/signalr so no WebSocket is opened in jsdom.
// The builder uses a plain class so `new HubConnectionBuilder()` reliably
// returns the builder stub (mockImplementation return values are unreliable
// for `new`-constructed calls in some module contexts).
// ---------------------------------------------------------------------------
vi.mock("@microsoft/signalr", () => {
  const HubConnectionState = {
    Disconnected: "Disconnected",
    Connecting: "Connecting",
    Connected: "Connected",
    Reconnecting: "Reconnecting",
    Disconnecting: "Disconnecting",
  } as const;

  // Plain async functions (not vi.fn) so the global afterEach
  // `vi.restoreAllMocks()` in tests/setup.ts cannot wipe the resolved-value
  // implementation between tests. ChannelsPage tests do not need to assert on
  // start/stop calls — only that they do not throw. The lifecycle event
  // registrations (on/onreconnecting/...) stay as `vi.fn()` because the hook
  // never reads their return value.
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

function renderPage(authOverride = fakeAuth, lang = "en") {
  void i18n.changeLanguage(lang);
  return render(
    <AuthContext.Provider value={authOverride}>
      <I18nextProvider i18n={i18n}>
        <MemoryRouter initialEntries={[`/t/acme/guilds/${GUILD_ID}/channels`]}>
          <Routes>
            <Route
              path="/t/:slug/guilds/:guildPublicId/channels"
              element={<ChannelsPage />}
            />
          </Routes>
        </MemoryRouter>
      </I18nextProvider>
    </AuthContext.Provider>,
  );
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe("ChannelsPage", () => {
  beforeEach(() => {
    // Use clearAllMocks (not restoreAllMocks) so the SignalR mock's
    // fakeConn.start / .stop keep their .mockResolvedValue implementations.
    vi.clearAllMocks();
  });

  it("renders list of channels with bridge checkboxes", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue({
        ok: true,
        status: 200,
        json: async () => ({ channels: baseChannels }),
      }),
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
      vi.fn().mockResolvedValue({
        ok: true,
        status: 200,
        json: async () => ({ channels: baseChannels }),
      }),
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

  it("bridge toggle calls POST with auth header", async () => {
    const fetchMock = vi
      .fn()
      .mockImplementation(async (_url: string, init?: RequestInit) => {
        if (init?.method === "POST") {
          return {
            ok: true,
            status: 202,
            json: async () => ({ backfillJobId: "job-1" }),
          };
        }
        return {
          ok: true,
          status: 200,
          json: async () => ({ channels: baseChannels }),
        };
      });
    vi.stubGlobal("fetch", fetchMock);

    renderPage();

    await waitFor(() => {
      expect(
        screen.getByTestId(`channel-bridge-toggle-${CH_TEXT}`),
      ).toBeInTheDocument();
    });

    fireEvent.click(screen.getByTestId(`channel-bridge-toggle-${CH_TEXT}`));

    await waitFor(() => {
      const postCall = fetchMock.mock.calls.find(
        (args: unknown[]) =>
          (args[1] as RequestInit)?.method === "POST" &&
          String(args[0]).includes(`/channels/${CH_TEXT}/bridge`),
      );
      expect(postCall).toBeDefined();
      const headers = (postCall![1] as RequestInit).headers as Record<
        string,
        string
      >;
      expect(headers["Authorization"]).toBe("Bearer test-token");
    });
  });

  it("bridge toggle reverts checkbox state on API 409 error", async () => {
    const fetchMock = vi
      .fn()
      .mockImplementation(async (_url: string, init?: RequestInit) => {
        if (init?.method === "POST") {
          return { ok: false, status: 409, json: async () => ({}) };
        }
        return {
          ok: true,
          status: 200,
          json: async () => ({ channels: baseChannels }),
        };
      });
    vi.stubGlobal("fetch", fetchMock);

    renderPage();

    await waitFor(() => {
      expect(
        screen.getByTestId(`channel-bridge-toggle-${CH_TEXT}`),
      ).not.toBeChecked();
    });

    fireEvent.click(screen.getByTestId(`channel-bridge-toggle-${CH_TEXT}`));

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
      vi.fn().mockResolvedValue({
        ok: true,
        status: 200,
        json: async () => ({ channels: channelsWithBackfill }),
      }),
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
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue({
        ok: false,
        status: 500,
        json: async () => ({}),
      }),
    );

    renderPage();

    await waitFor(() => {
      expect(screen.getByTestId("channels-error")).toBeInTheDocument();
    });
  });

  it("renders localized labels in English", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue({
        ok: true,
        status: 200,
        json: async () => ({ channels: baseChannels }),
      }),
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
      vi.fn().mockResolvedValue({
        ok: true,
        status: 200,
        json: async () => ({ channels: baseChannels }),
      }),
    );

    renderPage(fakeAuth, "de");

    await waitFor(() => {
      expect(screen.getByRole("heading", { level: 1 })).toHaveTextContent(
        "Kanäle",
      );
    });
  });
});
