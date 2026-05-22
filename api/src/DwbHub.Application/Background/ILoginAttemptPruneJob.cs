namespace DwbHub.Application.Background;

public interface ILoginAttemptPruneJob
{
    Task RunAsync(CancellationToken ct = default);
}
