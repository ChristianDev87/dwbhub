using System.Net;

namespace DwbHub.Application.Setup;

public interface ISetupService
{
    Task<SetupStatus> GetStatusAsync(CancellationToken ct = default);
    Task<SetupOutcome> CompleteAsync(SetupRequest request, IPAddress? ip, string? userAgent, CancellationToken ct = default);
}
