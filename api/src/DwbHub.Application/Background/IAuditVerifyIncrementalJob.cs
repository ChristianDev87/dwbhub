namespace DwbHub.Application.Background;

public interface IAuditVerifyIncrementalJob
{
    Task RunAsync(CancellationToken ct = default);
}
