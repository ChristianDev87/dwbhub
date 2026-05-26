namespace DwbHub.Application.Setup;

/// <summary>Current state of the one-time setup wizard.</summary>
/// <param name="Completed">True when the bootstrap lock has been consumed.</param>
/// <param name="CompletedAt">UTC timestamp of completion, or <c>null</c> if not yet completed.</param>
public sealed record SetupStatus(bool Completed, DateTimeOffset? CompletedAt);
