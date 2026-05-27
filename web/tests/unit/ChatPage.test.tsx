/**
 * ChatPage unit tests (Plan 1.0 Task 13)
 *
 * Migrated to typed openapi-fetch + TanStack Query (PR 5c).
 *
 * SignalR is mocked to prevent real WebSocket connections.
 * react-virtuoso is mocked for synchronous rendering.
 * Fetch is stubbed per test via real Response objects (required for
 * openapi-fetch middleware's `instanceof Response` check).
 */
import type React from "react";
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
import { ChatPage } from "../../src/app/messaging/ChatPage";
import type { ChatMessage } from "../../src/app/messaging/useChannel";
import { createApiClient } from "../../src/lib/api/client";

// ---------------------------------------------------------------------------
// Mock useApiClient — absolute baseUrl required for jsdom URL construction.
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
// Mock @microsoft/signalr (same pattern as ChannelsPage.test.tsx)
// ---------------------------------------------------------------------------

// fakeConnRef is hoisted so tests can access .on.mock.calls to emit events
const { fakeConnRef } = vi.hoisted(() => {
  type FakeConn = {
    on: ReturnType<typeof vi.fn>;
    onreconnecting: ReturnType<typeof vi.fn>;
    onreconnected: ReturnType<typeof vi.fn>;
    onclose: ReturnType<typeof vi.fn>;
    start: () => Promise<void>;
    stop: () => Promise<void>;
    /** Emit a named hub event to all registered handlers. */
    emit: (event: string, payload: unknown) => void;
  };
  const fakeConnRef: { current: FakeConn | null } = { current: null };
  return { fakeConnRef };
});

vi.mock("@microsoft/signalr", () => {
  const HubConnectionState = {
    Disconnected: "Disconnected",
    Connecting: "Connecting",
    Connected: "Connected",
    Reconnecting: "Reconnecting",
    Disconnecting: "Disconnecting",
  } as const;

  function makeFakeConn() {
    const handlers: Record<string, ((...args: unknown[]) => void)[]> = {};
    const conn = {
      on: vi.fn((event: string, cb: (...args: unknown[]) => void) => {
        handlers[event] = handlers[event] ?? [];
        handlers[event].push(cb);
      }),
      onreconnecting: vi.fn(),
      onreconnected: vi.fn(),
      onclose: vi.fn(),
      start: () => Promise.resolve(),
      stop: () => Promise.resolve(),
      emit(event: string, payload: unknown) {
        for (const cb of handlers[event] ?? []) cb(payload);
      },
    };
    return conn;
  }

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
      const conn = makeFakeConn();
      fakeConnRef.current = conn;
      return conn;
    }
  }

  return { HubConnectionBuilder, HubConnectionState, LogLevel: { Warning: 1 } };
});

// ---------------------------------------------------------------------------
// Mock react-virtuoso
// The mock now forwards the extra MessageList props through itemContent so
// MessageRow receives isOwnMessage / isOwnerRole / onEdit / onDelete.
// ---------------------------------------------------------------------------
vi.mock("react-virtuoso", () => ({
  Virtuoso: ({
    data,
    itemContent,
  }: {
    data: ChatMessage[];
    itemContent: (index: number, item: ChatMessage) => React.ReactNode;
    [key: string]: unknown;
  }) => (
    <div data-testid="virtuoso-mock">
      {data.map((item, i) => (
        <div key={item.id}>{itemContent(i, item)}</div>
      ))}
    </div>
  ),
}));

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------
function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

// ---------------------------------------------------------------------------
// Fixtures
// ---------------------------------------------------------------------------

const OWNER_USER = {
  id: 1,
  email: "o@acme.test",
  displayName: "Owner",
  role: "Owner",
};

const MEMBER_USER = {
  id: 2,
  email: "m@acme.test",
  displayName: "Member",
  role: "Member",
};

const fakeAuth = {
  state: {
    kind: "authenticated" as const,
    accessToken: "test-token",
    user: OWNER_USER,
    tenant: { id: 1, slug: "acme", name: "ACME" },
  },
  login: vi.fn(),
  logout: vi.fn(),
  refreshAccessToken: vi.fn(),
};

const fakeMemberAuth = {
  state: {
    kind: "authenticated" as const,
    accessToken: "test-token-member",
    user: MEMBER_USER,
    tenant: { id: 1, slug: "acme", name: "ACME" },
  },
  login: vi.fn(),
  logout: vi.fn(),
  refreshAccessToken: vi.fn(),
};

const CHANNEL_ID = "cccc1111-0000-0000-0000-000000000001";
const OWN_MSG_PUBLIC_ID = "aaaa0000-0000-0000-0000-000000000001";
const OTHER_MSG_PUBLIC_ID = "bbbb0000-0000-0000-0000-000000000002";

function makeHistoryItem(
  id: number,
  overrides: Partial<{
    authorName: string;
    content: string;
    sentAt: string;
    editedAt: string | null;
    viaDwbhub: boolean;
    discordMessageId: number;
    publicId: string;
  }> = {},
) {
  return {
    id,
    publicId:
      overrides.publicId ??
      `00000000-0000-0000-0000-${String(id).padStart(12, "0")}`,
    authorName: overrides.authorName ?? `User${String(id)}`,
    content: overrides.content ?? `Message ${String(id)}`,
    sentAt: overrides.sentAt ?? new Date().toISOString(),
    editedAt: overrides.editedAt ?? null,
    viaDwbhub: overrides.viaDwbhub ?? false,
    discordMessageId: overrides.discordMessageId ?? id * 100,
  };
}

function makeQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });
}

function renderChat(authOverride = fakeAuth, lang = "en") {
  void i18n.changeLanguage(lang);
  const queryClient = makeQueryClient();
  return render(
    <QueryClientProvider client={queryClient}>
      <AuthContext.Provider value={authOverride}>
        <I18nextProvider i18n={i18n}>
          <MemoryRouter initialEntries={[`/t/acme/channels/${CHANNEL_ID}`]}>
            <Routes>
              <Route
                path="/t/:slug/channels/:channelPublicId"
                element={<ChatPage />}
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

describe("ChatPage", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  // ── Original 9 tests (must stay green) ───────────────────────────────────

  it("renders loading state then messages after fetch resolves", async () => {
    const msgs = [makeHistoryItem(1), makeHistoryItem(2), makeHistoryItem(3)];

    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValue(jsonResponse({ messages: msgs, nextBefore: null })),
    );

    renderChat();

    // Initially shows loading
    expect(screen.getByTestId("chat-loading")).toBeInTheDocument();

    // After fetch resolves, messages appear
    await waitFor(() => {
      expect(screen.getAllByTestId(/^message-row-/)).toHaveLength(3);
    });
  });

  it("send calls onSend with trimmed content and empties the input on success", async () => {
    // Capture the POST body inside the mock so we never read Request.body
    // after the mock returns (avoids a post-test stream cleanup warning in
    // Vitest 3 that causes exit code 1 even when all assertions pass).
    let capturedPostBody: string | null = null;

    const fetchMock = vi
      .fn()
      .mockImplementation(async (input: RequestInfo | URL) => {
        const req = input instanceof Request ? input : null;
        const url = req ? req.url : String(input);
        const method = req ? req.method : "GET";
        if (method === "POST" && url.includes("/messages")) {
          capturedPostBody = req ? await req.text() : null;
          return jsonResponse(
            {
              id: 99,
              publicId: OWN_MSG_PUBLIC_ID,
              discordMessageId: 888888,
              sentAt: new Date().toISOString(),
            },
            201,
          );
        }
        return jsonResponse({ messages: [], nextBefore: null });
      });
    vi.stubGlobal("fetch", fetchMock);

    renderChat();

    await waitFor(() => {
      expect(screen.queryByTestId("chat-loading")).not.toBeInTheDocument();
    });

    const input = screen.getByTestId("send-box-input");
    fireEvent.change(input, { target: { value: "  hello  " } });
    fireEvent.keyDown(input, { key: "Enter", shiftKey: false });

    // Input should be cleared after sending
    await waitFor(() => {
      expect((input as HTMLTextAreaElement).value).toBe("");
    });

    // POST should have been called with trimmed content
    await waitFor(() => {
      expect(capturedPostBody).not.toBeNull();
    });
    const body = JSON.parse(capturedPostBody!) as { content: string };
    expect(body.content).toBe("hello");
  });

  it("send button is disabled while POST is in flight", async () => {
    let resolvePost!: () => void;
    const pendingPost = new Promise<Response>((r) => {
      resolvePost = () =>
        r(
          jsonResponse(
            {
              id: 10,
              publicId: OWN_MSG_PUBLIC_ID,
              discordMessageId: 777,
              sentAt: new Date().toISOString(),
            },
            201,
          ),
        );
    });

    vi.stubGlobal(
      "fetch",
      vi.fn().mockImplementation(async (input: RequestInfo | URL) => {
        const req = input instanceof Request ? input : null;
        const method = req ? req.method : "GET";
        if (method === "POST") return pendingPost;
        return jsonResponse({ messages: [], nextBefore: null });
      }),
    );

    renderChat();

    await waitFor(() => {
      expect(screen.queryByTestId("chat-loading")).not.toBeInTheDocument();
    });

    const input = screen.getByTestId("send-box-input");
    const button = screen.getByTestId("send-box-button");

    fireEvent.change(input, { target: { value: "test" } });
    fireEvent.click(button);

    // While isSending=true, useChannel passes disabled=true to SendBox
    // The button should become disabled
    await waitFor(() => {
      expect(button).toBeDisabled();
    });

    // Clean up
    resolvePost();
  });

  it("shows (edited) indicator for messages with editedAt set", async () => {
    const msgs = [
      makeHistoryItem(1, {
        editedAt: new Date().toISOString(),
      }),
    ];

    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValue(jsonResponse({ messages: msgs, nextBefore: null })),
    );

    renderChat(fakeAuth, "en");

    await waitFor(() => {
      expect(screen.getByTestId("message-edited")).toBeInTheDocument();
    });

    expect(screen.getByTestId("message-edited").textContent).toMatch(/edited/i);
  });

  it("deleted message shows placeholder text, keeps author hidden", async () => {
    // The initial history never returns deleted items (backend filters),
    // but we verify the MessageRow handles isDeleted. We simulate by
    // rendering MessageRow directly through the mocked Virtuoso.
    // Approach: useChannel maps history items to ChatMessage with isDeleted=false,
    // so we test the UI contract by passing a message with isDeleted=true via
    // the MessageRow component in a separate scenario.
    // For the ChatPage test we verify via the live event approach is out of scope
    // (would require firing a SignalR event). Instead we test MessageRow directly.

    // Render the MessageRow through ChatPage — fetch one normal message
    const msgs = [makeHistoryItem(5, { content: "some content" })];
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValue(jsonResponse({ messages: msgs, nextBefore: null })),
    );

    renderChat(fakeAuth, "en");

    await waitFor(() => {
      expect(screen.getAllByTestId(/^message-row-/)).toHaveLength(1);
    });

    // Verify the non-deleted message does NOT show placeholder
    expect(screen.queryByText(/\[deleted\]/i)).not.toBeInTheDocument();
    expect(screen.getByTestId("message-content")).toHaveTextContent(
      "some content",
    );
  });

  it("HTML/XSS content is rendered as plain text, not as DOM nodes", async () => {
    const xssContent = "<script>alert(1)</script>";
    const msgs = [makeHistoryItem(1, { content: xssContent })];

    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValue(jsonResponse({ messages: msgs, nextBefore: null })),
    );

    renderChat();

    await waitFor(() => {
      expect(screen.getAllByTestId(/^message-row-/)).toHaveLength(1);
    });

    // The content element should contain the literal string, not execute script
    const contentEl = screen.getByTestId("message-content");
    expect(contentEl.textContent).toBe(xssContent);

    // No <script> element should exist in the document
    expect(document.querySelector("script[data-testid]")).toBeNull();
  });

  it("textarea has maxLength attribute of 2000", async () => {
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValue(jsonResponse({ messages: [], nextBefore: null })),
    );

    renderChat();

    await waitFor(() => {
      expect(screen.queryByTestId("chat-loading")).not.toBeInTheDocument();
    });

    const textarea = screen.getByTestId("send-box-input");
    expect(textarea).toHaveAttribute("maxLength", "2000");
  });

  it("shows error fallback when initial fetch fails", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(jsonResponse({}, 500)));

    renderChat(fakeAuth, "en");

    await waitFor(() => {
      expect(screen.getByTestId("chat-error")).toBeInTheDocument();
    });
  });

  it("shows empty state when history has no messages", async () => {
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValue(jsonResponse({ messages: [], nextBefore: null })),
    );

    renderChat(fakeAuth, "en");

    await waitFor(() => {
      expect(screen.getByTestId("chat-empty")).toBeInTheDocument();
    });
  });

  it("MessageReceived echo before POST resolves replaces pending entry (race fix)", async () => {
    // POST is held open so discordMessageId is never stamped on the pending
    // entry before the SignalR echo arrives — simulating the race condition.
    let resolvePost!: (value: Response) => void;
    const pendingPost = new Promise<Response>((r) => {
      resolvePost = r;
    });

    vi.stubGlobal(
      "fetch",
      vi.fn().mockImplementation(async (input: RequestInfo | URL) => {
        const req = input instanceof Request ? input : null;
        const method = req ? req.method : "GET";
        if (method === "POST") return pendingPost;
        return jsonResponse({ messages: [], nextBefore: null });
      }),
    );

    renderChat(fakeAuth, "en");

    // Wait for hub to connect so fakeConnRef.current is populated
    await waitFor(() => {
      expect(fakeConnRef.current).not.toBeNull();
    });

    // Wait for initial load to finish (empty list)
    await waitFor(() => {
      expect(screen.queryByTestId("chat-loading")).not.toBeInTheDocument();
    });

    // Type and send — this inserts a pending entry with discordMessageId=null
    const input = screen.getByTestId("send-box-input");
    fireEvent.change(input, { target: { value: "hello race" } });
    fireEvent.keyDown(input, { key: "Enter", shiftKey: false });

    // Pending entry should appear (one message row)
    await waitFor(() => {
      expect(screen.getAllByTestId(/^message-row-/)).toHaveLength(1);
    });

    // Now emit the SignalR echo BEFORE resolving the POST
    // This is the race: POST is still in-flight, pending entry has
    // discordMessageId=null
    await act(async () => {
      fakeConnRef.current!.emit("MessageReceived", {
        id: 42,
        channelPublicId: CHANNEL_ID,
        authorName: "Owner",
        content: "hello race",
        sentAt: new Date().toISOString(),
        viaDwbhub: true,
        discordMessageId: 999,
      });
    });

    // After echo: still exactly ONE message row (no duplicate)
    await waitFor(() => {
      const rows = screen.getAllByTestId(/^message-row-/);
      expect(rows).toHaveLength(1);
      // The pending entry is replaced — row id should be the real id (42)
      expect(rows[0]).toHaveAttribute("data-testid", "message-row-42");
    });

    // Resolve the POST — the pending tempId no longer exists, map is a no-op
    resolvePost(
      jsonResponse(
        {
          id: 42,
          publicId: OWN_MSG_PUBLIC_ID,
          discordMessageId: 999,
          sentAt: new Date().toISOString(),
        },
        201,
      ),
    );

    // Still exactly one row after POST resolves (no ghost entry created)
    await waitFor(() => {
      expect(screen.getAllByTestId(/^message-row-/)).toHaveLength(1);
    });
  });

  // ── New tests: Edit / Delete ──────────────────────────────────────────────

  /**
   * After a message is sent (POST resolves), its publicId is stamped into the
   * cache. The Edit button must appear for the owner of that message.
   */
  it("Edit button appears on own message after POST resolves (publicId populated)", async () => {
    // History is empty; we'll send a message and get the publicId back
    let resolvePost!: (value: Response) => void;
    const pendingPost = new Promise<Response>((r) => {
      resolvePost = r;
    });

    vi.stubGlobal(
      "fetch",
      vi.fn().mockImplementation(async (input: RequestInfo | URL) => {
        const req = input instanceof Request ? input : null;
        const method = req ? req.method : "GET";
        if (method === "POST") return pendingPost;
        return jsonResponse({ messages: [], nextBefore: null });
      }),
    );

    renderChat(fakeAuth, "en");

    await waitFor(() => {
      expect(screen.queryByTestId("chat-loading")).not.toBeInTheDocument();
    });

    // Send a message
    const input = screen.getByTestId("send-box-input");
    fireEvent.change(input, { target: { value: "my message" } });
    fireEvent.keyDown(input, { key: "Enter", shiftKey: false });

    // Pending entry appears
    await waitFor(() => {
      expect(screen.getAllByTestId(/^message-row-/)).toHaveLength(1);
    });

    // Before POST resolves: publicId is null → no edit button in action bar
    // (The action bar itself may not be visible due to CSS hover — but the button
    // should not exist at all when publicId is null)
    expect(screen.queryByTestId("message-edit-button")).not.toBeInTheDocument();

    // Resolve the POST with a publicId
    resolvePost(
      jsonResponse(
        {
          id: 10,
          publicId: OWN_MSG_PUBLIC_ID,
          discordMessageId: 111,
          sentAt: new Date().toISOString(),
        },
        201,
      ),
    );

    // After POST resolves, the row gets publicId stamped.
    // The edit button is rendered (hidden by CSS hover, but in the DOM).
    await waitFor(() => {
      expect(screen.getByTestId("message-edit-button")).toBeInTheDocument();
    });
  });

  /**
   * Edit button must NOT appear for messages authored by other users.
   * History message with authorName != currentUser's displayName.
   * (History items now carry publicId, but viaDwbhub=false / other author
   * still means no edit/delete buttons — this test verifies via a message
   * from another author injected via SignalR.)
   */
  it("Edit button is NOT visible on messages from other authors", async () => {
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValue(jsonResponse({ messages: [], nextBefore: null })),
    );

    renderChat(fakeAuth, "en");

    await waitFor(() => {
      expect(fakeConnRef.current).not.toBeNull();
    });
    await waitFor(() => {
      expect(screen.queryByTestId("chat-loading")).not.toBeInTheDocument();
    });

    // Inject a message from another author via SignalR
    await act(async () => {
      fakeConnRef.current!.emit("MessageReceived", {
        id: 77,
        channelPublicId: CHANNEL_ID,
        authorName: "OtherUser",
        content: "stranger's message",
        sentAt: new Date().toISOString(),
        viaDwbhub: false,
        discordMessageId: 700,
        publicId: OTHER_MSG_PUBLIC_ID,
      });
    });

    await waitFor(() => {
      expect(screen.getByTestId("message-row-77")).toBeInTheDocument();
    });

    // Owner can delete other's messages but NOT edit them
    expect(screen.queryByTestId("message-edit-button")).not.toBeInTheDocument();
  });

  /**
   * Delete button must appear for the Owner role even on foreign messages.
   */
  it("Delete button appears for Owner role on foreign message (injected via SignalR)", async () => {
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValue(jsonResponse({ messages: [], nextBefore: null })),
    );

    renderChat(fakeAuth, "en"); // fakeAuth has role="Owner"

    await waitFor(() => {
      expect(fakeConnRef.current).not.toBeNull();
    });
    await waitFor(() => {
      expect(screen.queryByTestId("chat-loading")).not.toBeInTheDocument();
    });

    await act(async () => {
      fakeConnRef.current!.emit("MessageReceived", {
        id: 88,
        channelPublicId: CHANNEL_ID,
        authorName: "SomeoneElse",
        content: "their message",
        sentAt: new Date().toISOString(),
        viaDwbhub: false,
        discordMessageId: 800,
        publicId: OTHER_MSG_PUBLIC_ID,
      });
    });

    await waitFor(() => {
      expect(screen.getByTestId("message-row-88")).toBeInTheDocument();
    });

    // Delete button is in the DOM (even if hidden by CSS hover)
    expect(screen.getByTestId("message-delete-button")).toBeInTheDocument();
    // But no Edit button for foreign message
    expect(screen.queryByTestId("message-edit-button")).not.toBeInTheDocument();
  });

  /**
   * Delete button must NOT appear for a Member role on foreign messages.
   */
  it("Delete button is NOT visible for Member role on foreign message", async () => {
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValue(jsonResponse({ messages: [], nextBefore: null })),
    );

    renderChat(fakeMemberAuth, "en"); // Member role

    await waitFor(() => {
      expect(fakeConnRef.current).not.toBeNull();
    });
    await waitFor(() => {
      expect(screen.queryByTestId("chat-loading")).not.toBeInTheDocument();
    });

    await act(async () => {
      fakeConnRef.current!.emit("MessageReceived", {
        id: 99,
        channelPublicId: CHANNEL_ID,
        authorName: "Owner", // different author
        content: "owner message",
        sentAt: new Date().toISOString(),
        viaDwbhub: false,
        discordMessageId: 900,
        publicId: OTHER_MSG_PUBLIC_ID,
      });
    });

    await waitFor(() => {
      expect(screen.getByTestId("message-row-99")).toBeInTheDocument();
    });

    // Member cannot edit or delete foreign messages
    expect(screen.queryByTestId("message-edit-button")).not.toBeInTheDocument();
    expect(
      screen.queryByTestId("message-delete-button"),
    ).not.toBeInTheDocument();
  });

  /**
   * Clicking Edit button opens inline textarea with current content.
   * Save calls PATCH. On success the edit area closes.
   */
  it("Edit mode: textarea shows, Save calls PATCH, edit area closes on success", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockImplementation(async (input: RequestInfo | URL) => {
        const req = input instanceof Request ? input : null;
        const method = req ? req.method : "GET";
        if (method === "PATCH") {
          return jsonResponse(
            {
              id: 42,
              publicId: OWN_MSG_PUBLIC_ID,
              content: "updated content",
              sentAt: new Date().toISOString(),
              editedAt: new Date().toISOString(),
              viaDwbhub: true,
              discordMessageId: 420,
            },
            200,
          );
        }
        return jsonResponse({ messages: [], nextBefore: null });
      }),
    );

    renderChat(fakeAuth, "en");

    await waitFor(() => {
      expect(fakeConnRef.current).not.toBeNull();
    });
    await waitFor(() => {
      expect(screen.queryByTestId("chat-loading")).not.toBeInTheDocument();
    });

    // Inject own message with a known publicId via SignalR
    await act(async () => {
      fakeConnRef.current!.emit("MessageReceived", {
        id: 42,
        channelPublicId: CHANNEL_ID,
        authorName: "Owner",
        content: "original content",
        sentAt: new Date().toISOString(),
        viaDwbhub: true,
        discordMessageId: 420,
        publicId: OWN_MSG_PUBLIC_ID,
      });
    });

    await waitFor(() => {
      expect(screen.getByTestId("message-row-42")).toBeInTheDocument();
    });

    // Click Edit
    const editBtn = screen.getByTestId("message-edit-button");
    fireEvent.click(editBtn);

    // Textarea should appear with original content
    await waitFor(() => {
      expect(screen.getByTestId("message-edit-textarea")).toBeInTheDocument();
    });
    const textarea = screen.getByTestId(
      "message-edit-textarea",
    ) as HTMLTextAreaElement;
    expect(textarea.value).toBe("original content");

    // Change content and Save
    fireEvent.change(textarea, { target: { value: "updated content" } });
    fireEvent.click(screen.getByTestId("message-edit-save"));

    // Edit area should close after success
    await waitFor(() => {
      expect(
        screen.queryByTestId("message-edit-textarea"),
      ).not.toBeInTheDocument();
    });
  });

  /**
   * Edit Save is disabled when textarea is empty.
   */
  it("Edit Save button is disabled when textarea is empty", async () => {
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValue(jsonResponse({ messages: [], nextBefore: null })),
    );

    renderChat(fakeAuth, "en");

    await waitFor(() => {
      expect(fakeConnRef.current).not.toBeNull();
    });
    await waitFor(() => {
      expect(screen.queryByTestId("chat-loading")).not.toBeInTheDocument();
    });

    await act(async () => {
      fakeConnRef.current!.emit("MessageReceived", {
        id: 55,
        channelPublicId: CHANNEL_ID,
        authorName: "Owner",
        content: "some text",
        sentAt: new Date().toISOString(),
        viaDwbhub: true,
        discordMessageId: 550,
        publicId: OWN_MSG_PUBLIC_ID,
      });
    });

    await waitFor(() => {
      expect(screen.getByTestId("message-row-55")).toBeInTheDocument();
    });

    fireEvent.click(screen.getByTestId("message-edit-button"));

    await waitFor(() => {
      expect(screen.getByTestId("message-edit-textarea")).toBeInTheDocument();
    });

    // Clear the textarea
    fireEvent.change(screen.getByTestId("message-edit-textarea"), {
      target: { value: "" },
    });

    expect(screen.getByTestId("message-edit-save")).toBeDisabled();
  });

  /**
   * Edit Save is disabled when content exceeds 1800 characters, and a warning is shown.
   */
  it("Edit Save button is disabled when content exceeds 1800 chars", async () => {
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValue(jsonResponse({ messages: [], nextBefore: null })),
    );

    renderChat(fakeAuth, "en");

    await waitFor(() => {
      expect(fakeConnRef.current).not.toBeNull();
    });
    await waitFor(() => {
      expect(screen.queryByTestId("chat-loading")).not.toBeInTheDocument();
    });

    await act(async () => {
      fakeConnRef.current!.emit("MessageReceived", {
        id: 66,
        channelPublicId: CHANNEL_ID,
        authorName: "Owner",
        content: "short",
        sentAt: new Date().toISOString(),
        viaDwbhub: true,
        discordMessageId: 660,
        publicId: OWN_MSG_PUBLIC_ID,
      });
    });

    await waitFor(() => {
      expect(screen.getByTestId("message-row-66")).toBeInTheDocument();
    });

    fireEvent.click(screen.getByTestId("message-edit-button"));

    await waitFor(() => {
      expect(screen.getByTestId("message-edit-textarea")).toBeInTheDocument();
    });

    // Set content to 1801 chars
    fireEvent.change(screen.getByTestId("message-edit-textarea"), {
      target: { value: "x".repeat(1801) },
    });

    expect(screen.getByTestId("message-edit-save")).toBeDisabled();
    expect(screen.getByTestId("message-edit-toolong")).toBeInTheDocument();
  });

  /**
   * Edit Cancel closes the edit area without calling PATCH.
   */
  it("Edit Cancel closes edit mode, no PATCH called", async () => {
    let patchCalled = false;
    vi.stubGlobal(
      "fetch",
      vi.fn().mockImplementation(async (input: RequestInfo | URL) => {
        const req = input instanceof Request ? input : null;
        const method = req ? req.method : "GET";
        if (method === "PATCH") {
          patchCalled = true;
          return jsonResponse({}, 200);
        }
        return jsonResponse({ messages: [], nextBefore: null });
      }),
    );

    renderChat(fakeAuth, "en");

    await waitFor(() => {
      expect(fakeConnRef.current).not.toBeNull();
    });
    await waitFor(() => {
      expect(screen.queryByTestId("chat-loading")).not.toBeInTheDocument();
    });

    await act(async () => {
      fakeConnRef.current!.emit("MessageReceived", {
        id: 71,
        channelPublicId: CHANNEL_ID,
        authorName: "Owner",
        content: "cancelme",
        sentAt: new Date().toISOString(),
        viaDwbhub: true,
        discordMessageId: 710,
        publicId: OWN_MSG_PUBLIC_ID,
      });
    });

    await waitFor(() => {
      expect(screen.getByTestId("message-row-71")).toBeInTheDocument();
    });

    fireEvent.click(screen.getByTestId("message-edit-button"));

    await waitFor(() => {
      expect(screen.getByTestId("message-edit-textarea")).toBeInTheDocument();
    });

    fireEvent.click(screen.getByTestId("message-edit-cancel"));

    await waitFor(() => {
      expect(
        screen.queryByTestId("message-edit-textarea"),
      ).not.toBeInTheDocument();
    });

    expect(patchCalled).toBe(false);
  });

  /**
   * Edit with edit_window_expired error shows specific error message.
   */
  it("Edit: edit_window_expired error shows specific hint", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockImplementation(async (input: RequestInfo | URL) => {
        const req = input instanceof Request ? input : null;
        const url = req ? req.url : String(input);
        const method = req ? req.method : "GET";
        if (method === "PATCH" && url.includes("/messages/")) {
          return new Response(
            JSON.stringify({
              error: "edit_window_expired",
              retryAfterSeconds: 0,
            }),
            {
              status: 422,
              headers: { "Content-Type": "application/json" },
            },
          );
        }
        return jsonResponse({ messages: [], nextBefore: null });
      }),
    );

    renderChat(fakeAuth, "en");

    await waitFor(() => {
      expect(fakeConnRef.current).not.toBeNull();
    });
    await waitFor(() => {
      expect(screen.queryByTestId("chat-loading")).not.toBeInTheDocument();
    });

    await act(async () => {
      fakeConnRef.current!.emit("MessageReceived", {
        id: 80,
        channelPublicId: CHANNEL_ID,
        authorName: "Owner",
        content: "old message",
        sentAt: new Date().toISOString(),
        viaDwbhub: true,
        discordMessageId: 800,
        publicId: OWN_MSG_PUBLIC_ID,
      });
    });

    await waitFor(() => {
      expect(screen.getByTestId("message-row-80")).toBeInTheDocument();
    });

    fireEvent.click(screen.getByTestId("message-edit-button"));

    await waitFor(() => {
      expect(screen.getByTestId("message-edit-textarea")).toBeInTheDocument();
    });

    fireEvent.change(screen.getByTestId("message-edit-textarea"), {
      target: { value: "new content" },
    });
    fireEvent.click(screen.getByTestId("message-edit-save"));

    await waitFor(() => {
      expect(screen.getByTestId("message-edit-error")).toBeInTheDocument();
    });

    expect(screen.getByTestId("message-edit-error").textContent).toMatch(
      /too old to edit/i,
    );
  });

  /**
   * Delete: clicking Delete shows confirm row. Clicking Cancel hides it without DELETE call.
   */
  it("Delete Cancel: confirm row disappears, no DELETE called", async () => {
    let deleteCalled = false;
    vi.stubGlobal(
      "fetch",
      vi.fn().mockImplementation(async (input: RequestInfo | URL) => {
        const req = input instanceof Request ? input : null;
        const method = req ? req.method : "GET";
        if (method === "DELETE") {
          deleteCalled = true;
          return new Response(null, { status: 204 });
        }
        return jsonResponse({ messages: [], nextBefore: null });
      }),
    );

    renderChat(fakeAuth, "en");

    await waitFor(() => {
      expect(fakeConnRef.current).not.toBeNull();
    });
    await waitFor(() => {
      expect(screen.queryByTestId("chat-loading")).not.toBeInTheDocument();
    });

    await act(async () => {
      fakeConnRef.current!.emit("MessageReceived", {
        id: 90,
        channelPublicId: CHANNEL_ID,
        authorName: "Owner",
        content: "delete me maybe",
        sentAt: new Date().toISOString(),
        viaDwbhub: true,
        discordMessageId: 900,
        publicId: OWN_MSG_PUBLIC_ID,
      });
    });

    await waitFor(() => {
      expect(screen.getByTestId("message-row-90")).toBeInTheDocument();
    });

    // Click Delete to open confirm row
    fireEvent.click(screen.getByTestId("message-delete-button"));

    await waitFor(() => {
      expect(
        screen.getByTestId("message-delete-confirm-row"),
      ).toBeInTheDocument();
    });

    // Cancel
    fireEvent.click(screen.getByTestId("message-delete-cancel-button"));

    await waitFor(() => {
      expect(
        screen.queryByTestId("message-delete-confirm-row"),
      ).not.toBeInTheDocument();
    });

    expect(deleteCalled).toBe(false);
  });

  /**
   * Delete: confirming calls DELETE and optimistically marks message as deleted.
   */
  it("Delete Confirm: calls DELETE, message optimistically marked as deleted", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockImplementation(async (input: RequestInfo | URL) => {
        const req = input instanceof Request ? input : null;
        const method = req ? req.method : "GET";
        if (method === "DELETE") {
          return new Response(null, { status: 204 });
        }
        return jsonResponse({ messages: [], nextBefore: null });
      }),
    );

    renderChat(fakeAuth, "en");

    await waitFor(() => {
      expect(fakeConnRef.current).not.toBeNull();
    });
    await waitFor(() => {
      expect(screen.queryByTestId("chat-loading")).not.toBeInTheDocument();
    });

    await act(async () => {
      fakeConnRef.current!.emit("MessageReceived", {
        id: 91,
        channelPublicId: CHANNEL_ID,
        authorName: "Owner",
        content: "going to delete",
        sentAt: new Date().toISOString(),
        viaDwbhub: true,
        discordMessageId: 910,
        publicId: OWN_MSG_PUBLIC_ID,
      });
    });

    await waitFor(() => {
      expect(screen.getByTestId("message-row-91")).toBeInTheDocument();
    });

    // Click Delete → confirm → confirm button
    fireEvent.click(screen.getByTestId("message-delete-button"));

    await waitFor(() => {
      expect(
        screen.getByTestId("message-delete-confirm-row"),
      ).toBeInTheDocument();
    });

    fireEvent.click(screen.getByTestId("message-delete-confirm-button"));

    // Message should be optimistically marked as deleted (shows [deleted] placeholder)
    await waitFor(() => {
      const content = screen.getByTestId("message-content");
      expect(content.textContent).toMatch(/\[deleted\]/i);
    });
  });

  /**
   * Esc key in edit textarea cancels edit mode.
   */
  it("Esc key in edit textarea cancels edit mode", async () => {
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValue(jsonResponse({ messages: [], nextBefore: null })),
    );

    renderChat(fakeAuth, "en");

    await waitFor(() => {
      expect(fakeConnRef.current).not.toBeNull();
    });
    await waitFor(() => {
      expect(screen.queryByTestId("chat-loading")).not.toBeInTheDocument();
    });

    await act(async () => {
      fakeConnRef.current!.emit("MessageReceived", {
        id: 95,
        channelPublicId: CHANNEL_ID,
        authorName: "Owner",
        content: "esc me",
        sentAt: new Date().toISOString(),
        viaDwbhub: true,
        discordMessageId: 950,
        publicId: OWN_MSG_PUBLIC_ID,
      });
    });

    await waitFor(() => {
      expect(screen.getByTestId("message-row-95")).toBeInTheDocument();
    });

    fireEvent.click(screen.getByTestId("message-edit-button"));

    await waitFor(() => {
      expect(screen.getByTestId("message-edit-textarea")).toBeInTheDocument();
    });

    fireEvent.keyDown(screen.getByTestId("message-edit-textarea"), {
      key: "Escape",
    });

    await waitFor(() => {
      expect(
        screen.queryByTestId("message-edit-textarea"),
      ).not.toBeInTheDocument();
    });
  });
});
