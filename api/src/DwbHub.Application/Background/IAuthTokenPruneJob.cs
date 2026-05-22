namespace DwbHub.Application.Background;

public interface IAuthTokenPruneJob
{
    Task RunAsync(CancellationToken ct = default);
}
