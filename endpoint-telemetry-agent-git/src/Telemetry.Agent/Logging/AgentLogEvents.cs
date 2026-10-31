using Microsoft.Extensions.Logging;
using Telemetry.Shared;

namespace Telemetry.Agent.Logging;

public static class AgentLogEvents
{
    public static void LogServiceStarted(ILogger logger, int restartCount) =>
        logger.LogInformation("Agent service started. Restart count: {RestartCount}", restartCount);

    public static void LogServiceStopped(ILogger logger) =>
        logger.LogInformation("Agent service stopped.");

    public static void LogSampleCaptured(
        ILogger logger,
        Guid sampleId,
        long sequenceNumber,
        SessionState state,
        decimal cpu,
        int memMb,
        int queueDepth) =>
        logger.LogInformation(
            "Telemetry sample captured. SampleId: {SampleId}, SequenceNumber: {SequenceNumber}, SessionState: {SessionState}, CpuPercent: {CpuPercent}, MemoryUsedMb: {MemoryUsedMb}, QueueDepth: {QueueDepth}",
            sampleId,
            sequenceNumber,
            state,
            cpu,
            memMb,
            queueDepth);

    public static void LogSamplesDropped(ILogger logger, int droppedCount, long maxBytes) =>
        logger.LogWarning(
            "Offline storage dropped {DroppedCount} samples after reaching {MaxBytes} bytes.",
            droppedCount,
            maxBytes);

    public static void LogDeviceEnrolled(ILogger logger, string deviceId) =>
        logger.LogInformation("Device enrolled. DeviceId: {DeviceId}", deviceId);

    public static void LogBatchSent(ILogger logger, int sampleCount, int droppedCount, long firstSeq, long lastSeq) =>
        logger.LogInformation(
            "Telemetry batch sent. SampleCount: {SampleCount}, DroppedCount: {DroppedCount}, FirstSequenceNumber: {FirstSequenceNumber}, LastSequenceNumber: {LastSequenceNumber}",
            sampleCount,
            droppedCount,
            firstSeq,
            lastSeq);

    public static void LogConfirmationReceived(ILogger logger, int storedCount, int duplicateCount, int confirmedCount) =>
        logger.LogInformation(
            "Batch confirmation received. StoredCount: {StoredCount}, DuplicateCount: {DuplicateCount}, ConfirmedCount: {ConfirmedCount}",
            storedCount,
            duplicateCount,
            confirmedCount);

    public static void LogRetryScheduled(ILogger logger, int attempt, TimeSpan delay, string reason) =>
        logger.LogWarning(
            "Telemetry sync retry scheduled. Attempt: {Attempt}, Delay: {Delay}, Reason: {Reason}",
            attempt,
            delay,
            reason);

    public static void LogRequestRejected(ILogger logger, string endpoint, int statusCode, string reason) =>
        logger.LogWarning(
            "Telemetry request rejected. Endpoint: {Endpoint}, StatusCode: {StatusCode}, Reason: {Reason}",
            endpoint,
            statusCode,
            reason);

    public static void LogIpcMessageRejected(ILogger logger, string reason) =>
        logger.LogWarning("Session IPC message rejected. Reason: {Reason}", reason);
}
