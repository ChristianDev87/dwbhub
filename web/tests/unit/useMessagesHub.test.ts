/**
 * useMessagesHub unit tests (Plan 1.0 Task 12)
 *
 * Verifies the hook's connection lifecycle (connect on mount, no connect when
 * unauthenticated, disconnect on unmount) and event dispatch.
 */

import { describe, it, expect, vi, beforeEach } from "vitest";
import { renderHook, act } from "@testing-library/react";

// ---------------------------------------------------------------------------
// Shared mutable state — accessed by both the vi.mock factory and the tests.
// vi.hoisted ensures these refs exist at hoist time (before any imports run).
// ---------------------------------------------------------------------------

const { fakeConnRef, mockTokenRef } = vi.hoisted(() => {
  interface FakeConn {
    on: ReturnType<typeof vi.fn>;
    onreconnecting: ReturnType<typeof vi.fn>;
    onreconnected: ReturnType<typeof vi.fn>;
    onclose: ReturnType<typeof vi.fn>;
    start: ReturnType<typeof vi.fn>;
    stop: ReturnType<typeof vi.fn>;
    emit: (eventName: string, payload: unknown) => void;
  }
  const fakeConnRef: { current: FakeConn | null } = { current: null };
  const mockTokenRef: { current: string | null } = {
    current: "test-token",
  };
  return { fakeConnRef, mockTokenRef };
});

// ---------------------------------------------------------------------------
// Mock @microsoft/signalr.
// Uses a plain class for HubConnectionBuilder so `new` reliably instantiates
// a stub with all the chainable builder methods.
// ---------------------------------------------------------------------------
vi.mock("@microsoft/signalr", () => {
  const HubConnectionState = {
    Disconnected: "Disconnected",
    Connecting: "Connecting",
    Connected: "Connected",
    Reconnecting: "Reconnecting",
    Disconnecting: "Disconnecting",
  } as const;

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
      // Return whatever is currently in fakeConnRef at call time.
      return fakeConnRef.current;
    }
  }

  return { HubConnectionBuilder, HubConnectionState, LogLevel: { Warning: 1 } };
});

// ---------------------------------------------------------------------------
// Mock AuthContext so we can control the accessToken.
// ---------------------------------------------------------------------------
vi.mock("../../src/app/auth-context", () => ({
  useAuth: () => ({
    state: mockTokenRef.current
      ? {
          kind: "authenticated",
          accessToken: mockTokenRef.current,
          user: {
            id: 1,
            email: "t@t.test",
            displayName: "T",
            role: "Owner",
          },
          tenant: { id: 1, slug: "acme", name: "ACME" },
        }
      : { kind: "unauthenticated" },
    login: vi.fn(),
    logout: vi.fn(),
    refresh: vi.fn(),
  }),
}));

import { useMessagesHub } from "../../src/app/messaging/useMessagesHub";
import type { MessageEvent } from "../../src/app/messaging/messages-events";

// ---------------------------------------------------------------------------
// Fake connection factory
// ---------------------------------------------------------------------------

type EventHandler = (...args: unknown[]) => void;

interface FakeConn {
  _handlers: Record<string, EventHandler>;
  on: ReturnType<typeof vi.fn>;
  onreconnecting: ReturnType<typeof vi.fn>;
  onreconnected: ReturnType<typeof vi.fn>;
  onclose: ReturnType<typeof vi.fn>;
  start: ReturnType<typeof vi.fn>;
  stop: ReturnType<typeof vi.fn>;
  emit: (event: string, payload: unknown) => void;
}

function makeFakeConn(): FakeConn {
  const conn: FakeConn = {
    _handlers: {} as Record<string, EventHandler>,
    on: vi.fn((name: string, fn: EventHandler) => {
      conn._handlers[name] = fn;
    }),
    onreconnecting: vi.fn(),
    onreconnected: vi.fn(),
    onclose: vi.fn(),
    start: vi.fn().mockResolvedValue(undefined),
    stop: vi.fn().mockResolvedValue(undefined),
    emit(event: string, payload: unknown) {
      conn._handlers[event]?.(payload);
    },
  };
  return conn;
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe("useMessagesHub", () => {
  beforeEach(() => {
    vi.restoreAllMocks();
    fakeConnRef.current = makeFakeConn();
    mockTokenRef.current = "test-token";
  });

  it("calls conn.start() on mount when accessToken is present", async () => {
    const handler = vi.fn();
    renderHook(() => useMessagesHub(handler));

    await act(async () => {
      await Promise.resolve();
    });

    expect((fakeConnRef.current as FakeConn).start).toHaveBeenCalledOnce();
  });

  it("does NOT call conn.start() when unauthenticated", async () => {
    mockTokenRef.current = null;
    const handler = vi.fn();
    renderHook(() => useMessagesHub(handler));

    await act(async () => {
      await Promise.resolve();
    });

    // fakeConnRef.current was never passed to build() — start was never called
    expect((fakeConnRef.current as FakeConn).start).not.toHaveBeenCalled();
  });

  it("calls handler with MessageReceived event", async () => {
    const handler = vi.fn();
    renderHook(() => useMessagesHub(handler));

    await act(async () => {
      await Promise.resolve();
    });

    const payload = {
      id: 1,
      tenantId: 1,
      channelPublicId: "ch-1",
      authorName: "Alice",
      content: "Hello",
      sentAt: "2026-05-25T10:00:00Z",
      viaDwbhub: false,
      discordMessageId: 123456789,
    };

    act(() => {
      (fakeConnRef.current as FakeConn).emit("MessageReceived", payload);
    });

    expect(handler).toHaveBeenCalledWith({
      kind: "MessageReceived",
      payload,
    } satisfies MessageEvent);
  });

  it("calls handler with BackfillProgress event", async () => {
    const handler = vi.fn();
    renderHook(() => useMessagesHub(handler));

    await act(async () => {
      await Promise.resolve();
    });

    const payload = {
      channelPublicId: "ch-2",
      tenantId: 1,
      fetchedCount: 100,
      status: "running" as const,
    };

    act(() => {
      (fakeConnRef.current as FakeConn).emit("BackfillProgress", payload);
    });

    expect(handler).toHaveBeenCalledWith({
      kind: "BackfillProgress",
      payload,
    } satisfies MessageEvent);
  });

  it("calls handler with ChannelBridgeChanged event", async () => {
    const handler = vi.fn();
    renderHook(() => useMessagesHub(handler));

    await act(async () => {
      await Promise.resolve();
    });

    const payload = {
      channelPublicId: "ch-3",
      tenantId: 1,
      isBridged: true,
    };

    act(() => {
      (fakeConnRef.current as FakeConn).emit("ChannelBridgeChanged", payload);
    });

    expect(handler).toHaveBeenCalledWith({
      kind: "ChannelBridgeChanged",
      payload,
    } satisfies MessageEvent);
  });

  it("registers handlers for all 6 event kinds", async () => {
    const handler = vi.fn();
    renderHook(() => useMessagesHub(handler));

    await act(async () => {
      await Promise.resolve();
    });

    const conn = fakeConnRef.current as FakeConn;
    const registeredEvents = (conn.on.mock.calls as [string, unknown][]).map(
      ([name]) => name,
    );

    expect(registeredEvents).toContain("MessageReceived");
    expect(registeredEvents).toContain("MessageUpdated");
    expect(registeredEvents).toContain("MessageDeleted");
    expect(registeredEvents).toContain("BackfillProgress");
    expect(registeredEvents).toContain("BackfillComplete");
    expect(registeredEvents).toContain("ChannelBridgeChanged");
  });

  it("calls conn.stop() on unmount", async () => {
    const handler = vi.fn();
    const { unmount } = renderHook(() => useMessagesHub(handler));

    await act(async () => {
      await Promise.resolve();
    });

    unmount();

    expect((fakeConnRef.current as FakeConn).stop).toHaveBeenCalledOnce();
  });
});
