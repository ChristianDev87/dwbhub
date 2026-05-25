/**
 * useBackfillProgress — subscribes to BackfillProgress + BackfillComplete
 * events for a specific channel.
 *
 * Returns `{ status, fetchedCount }` updated in real-time via SignalR.
 * The caller is responsible for providing a stable `onEvent` ref or wrapping
 * this in an outer SignalR handler (see useChannelsList).
 */

import { useCallback, useState } from "react";
import type { MessageEvent } from "./messages-events";

export type BackfillStatus = "idle" | "running" | "completed" | "failed";

export interface BackfillState {
  status: BackfillStatus;
  fetchedCount: number;
}

/**
 * Returns `[state, handleEvent]`.
 *
 * Feed `handleEvent` into your useMessagesHub handler (filtered to the
 * relevant channel). The state object reflects the latest progress values.
 *
 * @param channelPublicId - The channel whose backfill progress to track.
 */
export function useBackfillProgress(channelPublicId: string): {
  state: BackfillState;
  handleEvent: (evt: MessageEvent) => void;
} {
  const [state, setState] = useState<BackfillState>({
    status: "idle",
    fetchedCount: 0,
  });

  const handleEvent = useCallback(
    (evt: MessageEvent) => {
      if (
        evt.kind === "BackfillProgress" &&
        evt.payload.channelPublicId === channelPublicId
      ) {
        setState({
          status: evt.payload.status as BackfillStatus,
          fetchedCount: evt.payload.fetchedCount,
        });
      } else if (
        evt.kind === "BackfillComplete" &&
        evt.payload.channelPublicId === channelPublicId
      ) {
        setState({
          status: "completed",
          fetchedCount: evt.payload.totalFetched,
        });
      }
    },
    [channelPublicId],
  );

  return { state, handleEvent };
}
