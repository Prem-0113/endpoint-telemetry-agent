using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Telemetry.Server.Services;
using Telemetry.Server.Storage;
using Telemetry.Shared;

namespace Telemetry.Server.Api;

public static class TelemetryEndpoints
{
    private const string EnrollEndpoint = "/api/v1/devices/enroll";
    private const string BatchEndpoint = "/api/v1/telemetry/batch";

    public static void MapTelemetryEndpoints(this WebApplication app)
    {
        app.MapPost(EnrollEndpoint, async Task<IResult> (
                HttpContext context,
                IServerTelemetryService service,
                CancellationToken ct) =>
            {
                EnrollRequest? request;
                try
                {
                    request = await JsonSerializer.DeserializeAsync<EnrollRequest>(
                        context.Request.Body,
                        TelemetryJsonOptions.Default,
                        ct).ConfigureAwait(false);
                }
                catch (JsonException)
                {
                    const string reason = "Request body is not valid enrollment JSON.";
                    await service.RejectAsync(EnrollEndpoint, 400, reason, null, DateTimeOffset.UtcNow, ct);
                    return TypedResults.Problem(reason, statusCode: 400, title: "Bad Request");
                }

                if (request is null ||
                    string.IsNullOrWhiteSpace(request.DeviceName) ||
                    string.IsNullOrWhiteSpace(request.MachineGuid) ||
                    string.IsNullOrWhiteSpace(request.AgentVersion))
                {
                    const string reason = "DeviceName, MachineGuid, and AgentVersion are required.";
                    await service.RejectAsync(EnrollEndpoint, 400, reason, null, DateTimeOffset.UtcNow, ct);
                    return TypedResults.Problem(reason, statusCode: 400, title: "Bad Request");
                }

                var response = await service.EnrollAsync(request, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
                return TypedResults.Ok(response);
            })
            .WithName("EnrollDevice")
            .WithSummary("Enroll an endpoint device")
            .WithDescription("Registers a device and returns its one-time API token.")
            .Produces<EnrollResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        app.MapPost(BatchEndpoint, async Task<IResult> (
                HttpContext context,
                IServerTelemetryService service,
                ServerOptions options,
                CancellationToken ct) =>
            {
                var deviceId = context.Request.Headers["X-Device-Id"].FirstOrDefault();
                var maxRequestBytes = Math.Max(1, options.MaxBatchRequestBytes);
                if (context.Request.ContentLength > maxRequestBytes)
                {
                    const string reason = "Payload exceeds maximum size limit";
                    await service.RejectAsync(BatchEndpoint, 413, reason, deviceId, DateTimeOffset.UtcNow, ct);
                    return TypedResults.Problem(reason, statusCode: 413, title: "Payload Too Large");
                }

                var body = await ReadLimitedBodyAsync(context.Request.Body, maxRequestBytes, ct)
                    .ConfigureAwait(false);
                if (body.Length > maxRequestBytes)
                {
                    const string reason = "Payload exceeds maximum size limit";
                    await service.RejectAsync(BatchEndpoint, 413, reason, deviceId, DateTimeOffset.UtcNow, ct);
                    return TypedResults.Problem(reason, statusCode: 413, title: "Payload Too Large");
                }

                var authorization = context.Request.Headers.Authorization.FirstOrDefault();
                var apiToken = authorization is not null &&
                               authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? authorization["Bearer ".Length..].Trim()
                    : string.Empty;
                if (string.IsNullOrWhiteSpace(deviceId) ||
                    string.IsNullOrWhiteSpace(apiToken) ||
                    !await service.ValidateCredentialsAsync(deviceId, apiToken, ct).ConfigureAwait(false))
                {
                    const string reason = "Missing or invalid device credentials";
                    await service.RejectAsync(BatchEndpoint, 401, reason, deviceId, DateTimeOffset.UtcNow, ct);
                    return TypedResults.Problem(reason, statusCode: 401, title: "Unauthorized");
                }

                BatchUploadRequest? request;
                try
                {
                    request = JsonSerializer.Deserialize<BatchUploadRequest>(body, TelemetryJsonOptions.Default);
                }
                catch (JsonException)
                {
                    const string reason = "Request body is not valid batch JSON.";
                    await service.RejectAsync(BatchEndpoint, 400, reason, deviceId, DateTimeOffset.UtcNow, ct);
                    return TypedResults.Problem(reason, statusCode: 400, title: "Bad Request");
                }

                if (request?.Samples is null || request.Samples.Count == 0)
                {
                    const string reason = "Batch must contain at least one sample.";
                    await service.RejectAsync(BatchEndpoint, 400, reason, deviceId, DateTimeOffset.UtcNow, ct);
                    return TypedResults.Problem(reason, statusCode: 400, title: "Bad Request");
                }

                if (request.Samples.Count > TelemetryConstants.MaxBatchSampleCount)
                {
                    var reason = $"Batch cannot contain more than {TelemetryConstants.MaxBatchSampleCount} samples.";
                    await service.RejectAsync(BatchEndpoint, 400, reason, deviceId, DateTimeOffset.UtcNow, ct);
                    return TypedResults.Problem(reason, statusCode: 400, title: "Bad Request");
                }

                if (!string.IsNullOrWhiteSpace(request.DeviceId) &&
                    !string.Equals(request.DeviceId, deviceId, StringComparison.Ordinal))
                {
                    const string reason = "Batch device ID does not match the authenticated device.";
                    await service.RejectAsync(BatchEndpoint, 400, reason, deviceId, DateTimeOffset.UtcNow, ct);
                    return TypedResults.Problem(reason, statusCode: 400, title: "Bad Request");
                }

                try
                {
                    var response = await service.IngestBatchAsync(deviceId!, request, DateTimeOffset.UtcNow, ct)
                        .ConfigureAwait(false);
                    return TypedResults.Ok(response);
                }
                catch (BatchValidationException exception)
                {
                    return TypedResults.Problem(exception.Message, statusCode: 400, title: "Bad Request");
                }
                catch (ArgumentException exception)
                {
                    await service.RejectAsync(BatchEndpoint, 400, exception.Message, deviceId, DateTimeOffset.UtcNow, ct);
                    return TypedResults.Problem(exception.Message, statusCode: 400, title: "Bad Request");
                }
                catch (KeyNotFoundException)
                {
                    const string reason = "Missing or invalid device credentials";
                    await service.RejectAsync(BatchEndpoint, 401, reason, deviceId, DateTimeOffset.UtcNow, ct);
                    return TypedResults.Problem(reason, statusCode: 401, title: "Unauthorized");
                }
            })
            .WithName("IngestTelemetryBatch")
            .WithSummary("Ingest a telemetry batch")
            .WithDescription("Authenticates a device and stores a validated, idempotent telemetry batch.")
            .Produces<BatchUploadResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge);

        app.MapGet("/api/v1/dashboard/overview", async Task<Ok<DashboardOverviewDto>> (
                string? deviceId,
                string? range,
                IDashboardQueryService dashboard,
                CancellationToken ct) =>
                TypedResults.Ok(await dashboard.GetOverviewAsync(
                    deviceId,
                    range,
                    DateTimeOffset.UtcNow,
                    ct).ConfigureAwait(false)))
            .WithName("GetDashboardOverview")
            .WithSummary("Get endpoint telemetry dashboard data")
            .WithDescription("Returns device health, samples, delivery, session, and application usage summaries.")
            .Produces<DashboardOverviewDto>(StatusCodes.Status200OK);

        app.MapGet("/api/v1/health", () => TypedResults.Ok(new HealthResponse("ok")))
            .WithName("GetHealth")
            .WithSummary("Check server health")
            .WithDescription("Returns the server's basic health status.")
            .Produces<HealthResponse>(StatusCodes.Status200OK);
    }

    private static async Task<byte[]> ReadLimitedBodyAsync(
        Stream requestBody,
        int maximumBytes,
        CancellationToken ct)
    {
        await using var buffer = new MemoryStream(Math.Min(maximumBytes, 81920));
        var chunk = new byte[81920];
        while (true)
        {
            var read = await requestBody.ReadAsync(chunk.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            var remainingAllowed = (long)maximumBytes + 1 - buffer.Length;
            var accepted = (int)Math.Min(read, Math.Max(0L, remainingAllowed));
            if (accepted > 0)
            {
                await buffer.WriteAsync(chunk.AsMemory(0, accepted), ct).ConfigureAwait(false);
            }

            if (buffer.Length > maximumBytes || accepted < read)
            {
                return buffer.ToArray();
            }
        }
    }

    /// <summary>Reports the basic health state of the telemetry server.</summary>
    private sealed record HealthResponse(string Status);
}
