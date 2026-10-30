using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Telemetry.Agent.Logging;
using Telemetry.Shared;

namespace Telemetry.Agent.Sync;

public sealed class HttpTelemetrySyncClient : ITelemetrySyncClient
{
    private const string EnrollEndpoint = "/api/v1/devices/enroll";
    private const string BatchEndpoint = "/api/v1/telemetry/batch";
    private readonly HttpClient _httpClient;
    private readonly ILogger<HttpTelemetrySyncClient>? _logger;

    public HttpTelemetrySyncClient(HttpClient httpClient, ILogger<HttpTelemetrySyncClient>? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger;
    }

    public async Task<EnrollResponse> EnrollAsync(EnrollRequest request, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, EnrollEndpoint)
        {
            Content = JsonContent(request)
        };
        using var response = await _httpClient.SendAsync(message, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            var reason = $"Enrollment returned HTTP {(int)response.StatusCode}.";
            AgentLogEvents.LogRequestRejected(
                _logger ?? NullLogger<HttpTelemetrySyncClient>.Instance,
                EnrollEndpoint,
                (int)response.StatusCode,
                reason);
            response.EnsureSuccessStatusCode();
            throw new HttpRequestException(reason);
        }

        return await response.Content.ReadFromJsonAsync<EnrollResponse>(TelemetryJsonOptions.Default, ct)
            .ConfigureAwait(false) ?? throw new InvalidDataException("Enrollment response was empty.");
    }

    public async Task<SyncUploadOutcome> UploadBatchAsync(
        BatchUploadRequest request,
        string apiToken,
        CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, BatchEndpoint)
        {
            Content = JsonContent(request)
        };
        message.Headers.Add("X-Device-Id", request.DeviceId);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(message, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return SyncUploadOutcome.Transient("The HTTP request timed out.");
        }
        catch (HttpRequestException exception)
        {
            return SyncUploadOutcome.Transient(exception.Message);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.OK)
            {
                try
                {
                    var result = await response.Content.ReadFromJsonAsync<BatchUploadResponse>(
                        TelemetryJsonOptions.Default,
                        ct).ConfigureAwait(false);
                    return result is null
                        ? SyncUploadOutcome.Rejected("The server returned an empty batch confirmation.", 200)
                        : SyncUploadOutcome.Succeeded(result);
                }
                catch (JsonException)
                {
                    return SyncUploadOutcome.Rejected("The server returned an invalid batch confirmation.", 200);
                }
            }

            var statusCode = (int)response.StatusCode;
            if (statusCode == 429 || statusCode >= 500)
            {
                return SyncUploadOutcome.Transient($"The server returned HTTP {statusCode}.", statusCode);
            }

            var reason = $"The server permanently rejected the batch with HTTP {statusCode}.";
            return SyncUploadOutcome.Rejected(reason, statusCode);
        }
    }

    private static StringContent JsonContent<T>(T value) =>
        new(JsonSerializer.Serialize(value, TelemetryJsonOptions.Default), Encoding.UTF8, "application/json");
}
