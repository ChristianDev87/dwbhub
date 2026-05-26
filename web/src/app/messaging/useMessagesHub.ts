/**
 * useMessagesHub — shared SignalR connection hook for the messages hub.
 *
 * Usage:
 *   const hubState = useMessagesHub(handler);
 *
 * StrictMode-safe + handler-stable:
 *  - The connection is created ONCE per accessToken via a cancellation flag,
 *    so StrictMode's double-mount tears down + rebuilds the SAME connection
 *    slot instead of racing.
 *  - The `handler` is held in a ref so the connection is NOT recreated when
 *    the caller passes a new handler reference (e.g. inline arrow). Callers
 *    no longer need to `useCallback` defensively.
 *  - The returned `connState` is `HubConnectionState.Connected` only after
 *    `conn.start()` has resolved. Callers that need to gate sends on a live
 *    connection can check this value.
 *
 * Multi-tenant isolation: the tenant group is assigned server-side from the
 * JWT claim — clients cannot subscribe to other tenants' broadcasts.
 *
 * Security: the access token is supplied via `accessTokenFactory` so it
 * never appears in the WebSocket URL query string.
 */

import {
  type HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from "@microsoft/signalr";
import { useEffect, useRef, useState } from "react";
import { useAuth } from "../auth-context";
import type { MessageEvent } from "./messages-events";

export type { MessageEvent };

/**
 * Subscribes to the SignalR messages hub for the authenticated tenant.
 *
 * Auto-reconnects on transient failures (0 ms, 2 s, 10 s, 30 s, 30 s).
 * Returns the current `HubConnectionState` for UI feedback (e.g. a
 * "Reconnecting…" banner when the state is `Reconnecting`).
 *
 * @param handler - Callback invoked for every received hub event. A ref
 *   is used internally, so callers do NOT need to `useCallback` to prevent
 *   spurious reconnects.
 */
export function useMessagesHub(
  handler: (evt: MessageEvent) => void,
): HubConnectionState {
  const { state } = useAuth();
  const accessToken = state.kind === "authenticated" ? state.accessToken : null;

  const [connState, setConnState] = useState<HubConnectionState>(
    HubConnectionState.Disconnected,
  );

  // Latest-handler ref: caller can pass a fresh closure every render without
  // forcing a reconnect.
  const handlerRef = useRef(handler);
  useEffect(() => {
    handlerRef.current = handler;
  }, [handler]);

  useEffect(() => {
    if (!accessToken) {
      setConnState(HubConnectionState.Disconnected);
      return;
    }

    // Cancellation flag — set true in the effect cleanup so the async start()
    // promise can detect that we were unmounted before connection finished
    // and refrain from stale-setState.
    let cancelled = false;

    const conn: HubConnection = new HubConnectionBuilder()
      .withUrl("/api/hubs/messages", {
        accessTokenFactory: () => accessToken,
      })
      .withAutomaticReconnect([0, 2000, 10000, 30000, 30000])
      .configureLogging(LogLevel.Warning)
      .build();

    // Use handlerRef so a re-rendered caller doesn't recreate the connection.
    conn.on("MessageReceived", (payload) =>
      handlerRef.current({ kind: "MessageReceived", payload }),
    );
    conn.on("MessageUpdated", (payload) =>
      handlerRef.current({ kind: "MessageUpdated", payload }),
    );
    conn.on("MessageDeleted", (payload) =>
      handlerRef.current({ kind: "MessageDeleted", payload }),
    );
    conn.on("BackfillProgress", (payload) =>
      handlerRef.current({ kind: "BackfillProgress", payload }),
    );
    conn.on("BackfillComplete", (payload) =>
      handlerRef.current({ kind: "BackfillComplete", payload }),
    );
    conn.on("ChannelBridgeChanged", (payload) =>
      handlerRef.current({ kind: "ChannelBridgeChanged", payload }),
    );

    conn.onreconnecting(() => {
      if (!cancelled) setConnState(HubConnectionState.Reconnecting);
    });
    conn.onreconnected(() => {
      if (!cancelled) setConnState(HubConnectionState.Connected);
    });
    conn.onclose(() => {
      if (!cancelled) setConnState(HubConnectionState.Disconnected);
    });

    setConnState(HubConnectionState.Connecting);

    conn
      .start()
      .then(() => {
        if (cancelled) {
          // We were unmounted before start finished — stop immediately so we
          // don't leak a half-open connection and don't push stale state.
          void conn.stop().catch(() => {});
          return;
        }
        setConnState(HubConnectionState.Connected);
      })
      .catch((err: unknown) => {
        if (cancelled) return;
        console.error("SignalR connect failed", err);
        setConnState(HubConnectionState.Disconnected);
      });

    return () => {
      cancelled = true;
      void conn.stop().catch(() => {
        // intentional: ignore stop errors during cleanup
      });
    };
  }, [accessToken]); // ← handler intentionally OMITTED — captured via ref

  return connState;
}
