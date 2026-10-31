namespace Telemetry.Agent.Collectors;

public interface IMachineMetricsProvider
{
    (decimal CpuPercent, int MemoryUsedMb) GetMetrics();
}
