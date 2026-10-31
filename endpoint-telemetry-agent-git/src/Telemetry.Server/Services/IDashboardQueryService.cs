using Telemetry.Shared;

namespace Telemetry.Server.Services;

public interface IDashboardQueryService
{
    Task<DashboardOverviewDto> GetOverviewAsync(
        string? deviceId,
        string? range,
        DateTimeOffset nowUtc,
        CancellationToken ct = default);
}
