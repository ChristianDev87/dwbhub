/**
 * SignalR hub event types for the messages hub (/api/hubs/messages).
 *
 * Each variant maps to a server-side `IMessagesBroadcaster` method.
 * Payloads reflect the JSON shapes the backend serialises over the wire.
 *
 * Discord snowflakes (discordChannelId, discordMessageId) arrive as JSON
 * numbers from the backend (long → number). They may exceed 2^53 in theory
 * but current Discord snowflakes fit safely. Store as-is; call `.toString()`
 * only when displaying or comparing as strings.
 */

export interface MessageReceivedPayload {
  id: number;
  tenantId: number;
  channelPublicId: string;
  authorName: string;
  content: string;
  sentAt: string;
  viaDwbhub: boolean;
  discordMessageId: number | string;
}

export interface MessageUpdatedPayload {
  /** Discord snowflake id of the updated message. Match against ChatMessage.discordMessageId. */
  messageId: number | string;
  content: string;
  editedAt: string;
}

export interface MessageDeletedPayload {
  /** Discord snowflake id of the deleted message. Match against ChatMessage.discordMessageId. */
  messageId: number | string;
}

export interface BackfillProgressPayload {
  channelPublicId: string;
  tenantId: number;
  fetchedCount: number;
}

export interface BackfillCompletePayload {
  channelPublicId: string;
  tenantId: number;
  totalFetched: number;
}

export interface ChannelBridgeChangedPayload {
  channelPublicId: string;
  tenantId: number;
  isBridged: boolean;
}

export type MessageEvent =
  | { kind: "MessageReceived"; payload: MessageReceivedPayload }
  | { kind: "MessageUpdated"; payload: MessageUpdatedPayload }
  | { kind: "MessageDeleted"; payload: MessageDeletedPayload }
  | { kind: "BackfillProgress"; payload: BackfillProgressPayload }
  | { kind: "BackfillComplete"; payload: BackfillCompletePayload }
  | { kind: "ChannelBridgeChanged"; payload: ChannelBridgeChangedPayload };
