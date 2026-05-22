namespace DwbHub.Application.Background;

public interface IAuditVerifyFullJob
{
    Task RunAsync(CancellationToken ct = default);
}
