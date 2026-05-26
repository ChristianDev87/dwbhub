namespace DwbHub.Application.Messaging;

/// <summary>
/// Result of <see cref="IMessageService.DeleteAsync"/>. Discriminated union.
/// </summary>
public abstract record DeleteMessageOutcome
{
    private DeleteMessageOutcome() { }

    /// <summary>The message was deleted successfully (on Discord + locally).</summary>
    public sealed record Success : DeleteMessageOutcome;

    public sealed record NotFound : DeleteMessageOutcome;
    public sealed record Forbidden : DeleteMessageOutcome;
    public sealed record AlreadyDeleted : DeleteMessageOutcome;

    /// <summary>
    /// Owner-moderation delete on an inbound (Discord-user) message requires the bot to have
    /// MANAGE_MESSAGES on the guild. Returned when the cached permission is FALSE or NULL.
    /// </summary>
    public sealed record BotMissingPermission : DeleteMessageOutcome;

    public sealed record DiscordError(string Reason) : DeleteMessageOutcome;
}
