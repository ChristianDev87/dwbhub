/**
 * useMessagesHub — shared SignalR connection hook for the messages hub.
 *
 * Usage:
 *   const hubState = useMessagesHub(handler);
 *
 * IMPORTANT: `handler` must be wrapped in `useCallback` by the caller.
 * The hook's useEffect depends on [accessToken, handler], so an unstable
 * (non-memoised) handler reference will cause the connection to tear down
 * and reconnect on every render.
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
 * @param handler - Stable callback (wrap in `useCallback`!) invoked for
 *   every received hub event.
 */
export function useMessagesHub(
  handler: (evt: MessageEvent) => void,
): HubConnectionState {
  const { state } = useAuth();
  const accessToken = state.kind === "authenticated" ? state.accessToken : null;

  const [connState, setConnState] = useState<HubConnectionState>(
    HubConnectionState.Disconnected,
  );
  const connRef = useRef<HubConnection | null>(null);

  useEffect(() => {
    if (!accessToken) return;

    const conn = new HubConnectionBuilder()
      .withUrl("/api/hubs/messages", {
        accessTokenFactory: () => {
          // accessTokenFactory captures the token at connection-build time. A new token
          // from AuthContext causes this effect to re-run (see dep array below) and
          // rebuild the connection with the fresh token. The factory itself is NOT
          // re-read on every call.
          return accessToken;
        },
      })
      .withAutomaticReconnect([0, 2000, 10000, 30000, 30000])
      .configureLogging(LogLevel.Warning)
      .build();

    conn.on("MessageReceived", (payload) =>
      handler({ kind: "MessageReceived", payload }),
    );
    conn.on("MessageUpdated", (payload) =>
      handler({ kind: "MessageUpdated", payload }),
    );
    conn.on("MessageDeleted", (payload) =>
      handler({ kind: "MessageDeleted", payload }),
    );
    conn.on("BackfillProgress", (payload) =>
      handler({ kind: "BackfillProgress", payload }),
    );
    conn.on("BackfillComplete", (payload) =>
      handler({ kind: "BackfillComplete", payload }),
    );
    conn.on("ChannelBridgeChanged", (payload) =>
      handler({ kind: "ChannelBridgeChanged", payload }),
    );

    conn.onreconnecting(() => setConnState(HubConnectionState.Reconnecting));
    conn.onreconnected(() => setConnState(HubConnectionState.Connected));
    conn.onclose(() => setConnState(HubConnectionState.Disconnected));

    connRef.current = conn;
    setConnState(HubConnectionState.Connecting);

    conn
      .start()
      .then(() => {
        setConnState(HubConnectionState.Connected);
      })
      .catch((err: unknown) => {
        console.error("SignalR connect failed", err);
        setConnState(HubConnectionState.Disconnected);
      });

    return () => {
      connRef.current = null;
      conn.stop().catch(() => {
        // intentional: ignore stop errors during cleanup
      });
    };
  }, [accessToken, handler]);

  return connState;
}
