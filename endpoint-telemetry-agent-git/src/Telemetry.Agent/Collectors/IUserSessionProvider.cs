using Telemetry.Shared;

namespace Telemetry.Agent.Collectors;

public interface IUserSessionProvider
{
    Task<UserSessionSnapshot> GetSnapshotAsync(DateTimeOffset nowUtc, CancellationToken ct = default);
}
