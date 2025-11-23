using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using CloudClipboard.Agent.Windows.Options;
using CloudClipboard.Core.Models;
using CloudClipboard.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudClipboard.Agent.Windows.Services;

public sealed class HttpCloudClipboardClient : ICloudClipboardClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<AgentOptions> _optionsMonitor;
    private readonly ClipboardPayloadSerializer _serializer;
    private readonly ILogger<HttpCloudClipboardClient> _logger;
    private IDisposable? _optionsSubscription;
    private volatile Uri? _apiBaseUri;

    public HttpCloudClipboardClient(
        HttpClient httpClient,
        IOptionsMonitor<AgentOptions> options,
        ClipboardPayloadSerializer serializer,
        ILogger<HttpCloudClipboardClient> logger)
    {
        _httpClient = httpClient;
        _serializer = serializer;
        _logger = logger;
        _optionsMonitor = options;
        ApplyBaseAddress(options.CurrentValue);
        _optionsSubscription = _optionsMonitor.OnChange(ApplyBaseAddress);
    }

    public async Task<ClipboardItemDto?> UploadAsync(ClipboardUploadRequest request, CancellationToken cancellationToken = default)
    {
        var payload = await _serializer.SerializeAsync(request.Payload, cancellationToken);
        await using (payload.Stream)
        {
            using var buffer = new MemoryStream();
            await payload.Stream.CopyToAsync(buffer, cancellationToken);
            var dto = new UploadEnvelope(
                request.OwnerId,
                request.Payload.PayloadType,
                Convert.ToBase64String(buffer.ToArray()),
                payload.ContentType,
                request.FileName,
                request.DeviceName,
                request.Payload.ExpiresUtc);

            using var httpRequest = CreateRequest(HttpMethod.Post, "clipboard/upload");
            httpRequest.Content = JsonContent.Create(dto);

            var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Upload failed: {Status} {Body}", response.StatusCode, body);
                response.EnsureSuccessStatusCode();
            }

            return await response.Content.ReadFromJsonAsync<ClipboardItemDto>(cancellationToken: cancellationToken);
        }
    }

    public async Task<IReadOnlyList<ClipboardItemDto>> ListAsync(string ownerId, int take, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, $"clipboard/{Escape(ownerId)}?take={take}");
        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var items = await response.Content.ReadFromJsonAsync<IReadOnlyList<ClipboardItemDto>>(cancellationToken: cancellationToken);
        return items ?? Array.Empty<ClipboardItemDto>();
    }

    public async Task<ClipboardItemDto?> DownloadAsync(string ownerId, string itemId, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, $"clipboard/{Escape(ownerId)}/{Escape(itemId)}");
        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ClipboardItemDto>(cancellationToken: cancellationToken);
    }

    public async Task<ClipboardOwnerState> GetStateAsync(string ownerId, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, $"clipboard/owners/{Escape(ownerId)}/state");
        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new ClipboardOwnerState(ownerId, false, null);
        }

        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<StateEnvelope>(cancellationToken: cancellationToken);
        return dto is null ? new ClipboardOwnerState(ownerId, false, null) : dto.ToModel();
    }

    public async Task<ClipboardOwnerState> SetStateAsync(string ownerId, bool isPaused, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, $"clipboard/owners/{Escape(ownerId)}/state");
        request.Content = JsonContent.Create(new StateEnvelope(ownerId, isPaused, null));
        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<StateEnvelope>(cancellationToken: cancellationToken);
        return dto is null ? new ClipboardOwnerState(ownerId, isPaused, null) : dto.ToModel();
    }

    public async Task DeleteOwnerAsync(string ownerId, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Delete, $"clipboard/{Escape(ownerId)}");
        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<ClipboardNotificationEvent>> PollNotificationsAsync(string ownerId, int timeoutSeconds, CancellationToken cancellationToken = default)
    {
        // The Functions endpoint blocks up to timeoutSeconds, letting us approximate server push without WebSockets.
        using var request = CreateRequest(HttpMethod.Get, $"clipboard/owners/{Escape(ownerId)}/notifications?timeoutSeconds={timeoutSeconds}");
        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            return Array.Empty<ClipboardNotificationEvent>();
        }

        response.EnsureSuccessStatusCode();
        var envelope = await response.Content.ReadFromJsonAsync<NotificationEnvelope>(cancellationToken: cancellationToken);
        return envelope?.Events ?? Array.Empty<ClipboardNotificationEvent>();
    }

    public async Task<NotificationConnectionInfo?> GetNotificationConnectionAsync(string ownerId, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, $"clipboard/owners/{Escape(ownerId)}/notifications/negotiate");
        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<NotificationConnectionInfo>(cancellationToken: cancellationToken);
    }

    public async Task<OwnerConfiguration?> GetOwnerConfigurationAsync(string ownerId, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, $"clipboard/owners/{Escape(ownerId)}/configuration");
        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<OwnerConfigurationDto>(cancellationToken: cancellationToken);
        return dto?.ToModel();
    }

    public async Task<OwnerConfiguration> SetOwnerConfigurationAsync(string ownerId, string configurationJson, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, $"clipboard/owners/{Escape(ownerId)}/configuration");
        request.Content = JsonContent.Create(new OwnerConfigurationRequest(configurationJson));
        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<OwnerConfigurationDto>(cancellationToken: cancellationToken);
        if (dto is null)
        {
            throw new InvalidOperationException("Configuration endpoint returned an empty payload.");
        }

        return dto.ToModel();
    }

    private sealed record NotificationEnvelope
    {
        public IReadOnlyList<ClipboardNotificationEvent> Events { get; init; } = Array.Empty<ClipboardNotificationEvent>();
    }
    private void ApplyBaseAddress(AgentOptions options)
    {
        if (Uri.TryCreate(options.ApiBaseUrl, UriKind.Absolute, out var baseAddress))
        {
            _apiBaseUri = baseAddress;
            if (_httpClient.BaseAddress is null)
            {
                _httpClient.BaseAddress = baseAddress;
            }
            else if (_httpClient.BaseAddress != baseAddress)
            {
                _logger.LogInformation("ApiBaseUrl changed to {ApiBaseUrl}; future requests will use the new endpoint.", baseAddress);
            }
            return;
        }

        _logger.LogWarning("Invalid ApiBaseUrl configured: {ApiBaseUrl}", options.ApiBaseUrl);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string relativeUri)
    {
        var normalized = relativeUri.TrimStart('/');
        if (!normalized.StartsWith("api/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = $"api/{normalized}";
        }

        normalized = AppendFunctionKey(normalized);
        var requestUri = BuildRequestUri(normalized);

        return new HttpRequestMessage(method, requestUri);
    }

    private string AppendFunctionKey(string relativeUri)
    {
        var key = _optionsMonitor.CurrentValue.FunctionKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            return relativeUri;
        }

        var separator = relativeUri.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{relativeUri}{separator}code={Uri.EscapeDataString(key)}";
    }

    private Uri BuildRequestUri(string relativeUri)
    {
        var baseUri = _apiBaseUri;
        if (baseUri is not null && Uri.TryCreate(baseUri, relativeUri, out var absolute))
        {
            return absolute;
        }

        return new Uri(relativeUri, UriKind.RelativeOrAbsolute);
    }

    public void Dispose()
    {
        _optionsSubscription?.Dispose();
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private sealed record UploadEnvelope(
        string OwnerId,
        ClipboardPayloadType PayloadType,
        string ContentBase64,
        string ContentType,
        string? FileName,
        string? DeviceName,
        DateTimeOffset? ExpiresUtc);

    private sealed record StateEnvelope(string OwnerId, bool IsPaused, string? UpdatedUtc)
    {
        public ClipboardOwnerState ToModel()
        {
            DateTimeOffset? updated = null;
            if (!string.IsNullOrWhiteSpace(UpdatedUtc) && DateTimeOffset.TryParse(UpdatedUtc, out var parsed))
            {
                updated = parsed;
            }

            return new ClipboardOwnerState(OwnerId, IsPaused, updated);
        }
    }

    private sealed record OwnerConfigurationDto(string OwnerId, string ConfigurationJson, DateTimeOffset UpdatedUtc)
    {
        public OwnerConfiguration ToModel() => new(OwnerId, ConfigurationJson, UpdatedUtc);
    }

    private sealed record OwnerConfigurationRequest(string ConfigurationJson);
}
