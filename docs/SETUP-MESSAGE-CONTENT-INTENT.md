# Discord MessageContent Intent (Privileged)

To enable DwbHub's chat-bridge features (Plan 1.0+), the bot's owner must
explicitly enable the **MessageContent** privileged gateway intent in the
Discord Developer Portal:

1. Open https://discord.com/developers/applications
2. Select your bot's application
3. Click **Bot** in the left sidebar
4. Scroll to **Privileged Gateway Intents**
5. Toggle on **MESSAGE CONTENT INTENT**
6. Save changes

This intent is only required while the bot is in fewer than 100 servers.
At 100+ servers Discord requires a formal application review — out of scope
for self-hosted single-tenant DwbHub deployments.

## What happens if this is missing?

The bot will still connect, but `MessageReceived` events will arrive with
empty `content` strings. DwbHub surfaces a clear error when bridging a
channel under this condition and refuses to enable the bridge until the
intent is granted.

## Related

- Plan 1.0 §3.3 — gateway intents architecture
- Plan 0.7 — bot token encryption (the credentials this intent applies to)
