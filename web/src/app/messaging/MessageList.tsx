/**
 * MessageList — virtualized list of chat messages using react-virtuoso.
 *
 * Behaviours:
 *   - Starts scrolled to the bottom (initialTopMostItemIndex = last item)
 *   - Auto-scrolls when user is already at the bottom (followOutput="auto")
 *   - Fires onLoadOlder when the user scrolls to the top (startReached)
 *   - Reports atBottom state changes for the new-message badge
 */

import type React from "react";
import { Virtuoso } from "react-virtuoso";
import type { ChatMessage } from "./useChannel";
import type { MessageActionPermissions } from "./MessageActionsMenu";
import { MessageRow } from "./MessageRow";

interface MessageListProps {
  messages: ChatMessage[];
  hasMore: boolean;
  onLoadOlder: () => void;
  onAtBottomChange: (atBottom: boolean) => void;
  getPermissions?: (msg: ChatMessage) => MessageActionPermissions;
  onEdit?: (messageId: number, content: string) => Promise<void>;
  onDelete?: (messageId: number) => void;
}

export function MessageList({
  messages,
  hasMore,
  onLoadOlder,
  onAtBottomChange,
  getPermissions,
  onEdit,
  onDelete,
}: MessageListProps): React.JSX.Element {
  // startReached must be omitted entirely (not passed as undefined) because
  // the project uses exactOptionalPropertyTypes: true.
  const startReachedProp = hasMore
    ? { startReached: (_index: number) => onLoadOlder() }
    : {};

  return (
    <Virtuoso
      style={{ flex: 1 }}
      data={messages}
      initialTopMostItemIndex={messages.length > 0 ? messages.length - 1 : 0}
      followOutput="auto"
      {...startReachedProp}
      atBottomStateChange={onAtBottomChange}
      itemContent={(_index: number, item: ChatMessage) => {
        const perms = getPermissions?.(item);
        return (
          <MessageRow
            key={item.id}
            message={item}
            {...(perms !== undefined ? { permissions: perms } : {})}
            {...(onEdit !== undefined ? { onEdit } : {})}
            {...(onDelete !== undefined ? { onDelete } : {})}
          />
        );
      }}
    />
  );
}
