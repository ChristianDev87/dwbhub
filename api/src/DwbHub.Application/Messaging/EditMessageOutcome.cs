namespace DwbHub.Application.Messaging;

using DwbHub.Core.Messaging;

/// <summary>
/// Result of <see cref="IMessageService.EditAsync"/>. Discriminated union — exactly one case is returned per call.
/// Pattern matches <c>LoginOutcome</c> / <c>RefreshOutcome</c> / <c>ManualReconnectOutcome</c> elsewhere in the codebase.
/// </summary>
public abstract record EditMessageOutcome
{
    private EditMessageOutcome() { }

    /// <summary>The message was edited successfully on Discord and persisted locally.</summary>
    public sealed record Success(Message Message) : EditMessageOutcome;

    /// <summary>No message exists with the given ID in the caller's tenant.</summary>
    public sealed record NotFound : EditMessageOutcome;

    /// <summary>Caller is not the author (and edit is author-only).</summary>
    public sealed record Forbidden : EditMessageOutcome;

    /// <summary>The per-tenant edit window has elapsed since the message was sent.</summary>
    public sealed record EditWindowExpired(int AgeSeconds, int WindowSeconds) : EditMessageOutcome;

    /// <summary>The message has already been soft-deleted.</summary>
    public sealed record AlreadyDeleted : EditMessageOutcome;

    /// <summary>The proposed new content is empty (after trim).</summary>
    public sealed record EmptyContent : EditMessageOutcome;

    /// <summary>The proposed new content exceeds Discord's 2000-character limit.</summary>
    public sealed record ContentTooLong(int Length, int Max) : EditMessageOutcome;

    /// <summary>The Discord webhook PATCH call failed (e.g., webhook deleted, upstream 5xx).</summary>
    public sealed record DiscordError(string Reason) : EditMessageOutcome;
}
