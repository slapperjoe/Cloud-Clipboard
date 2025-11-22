using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CloudClipboard.Core.Abstractions;
using CloudClipboard.Core.Models;
using CloudClipboard.Core.Services;
using CloudClipboard.Functions.Dtos;
using CloudClipboard.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudClipboard.Functions.Functions;

public sealed class ClipboardFunctions
{
    private readonly ClipboardCoordinator _coordinator;
    private readonly IClipboardMetadataStore _metadataStore;
    private readonly IClipboardPayloadStore _payloadStore;
    private readonly IClipboardOwnerStateStore _ownerStateStore;
    private readonly IClipboardNotificationService _notificationService;
    private readonly IRealtimeClipboardNotificationService _realtimeNotifications;
    private readonly ILogger _logger;

    public ClipboardFunctions(
        ClipboardCoordinator coordinator,
        IClipboardMetadataStore metadataStore,
        IClipboardPayloadStore payloadStore,
        IClipboardOwnerStateStore ownerStateStore,
        IClipboardNotificationService notificationService,
        ILogger<ClipboardFunctions> logger,
        IRealtimeClipboardNotificationService realtimeNotifications)
    {
        _coordinator = coordinator;
        _metadataStore = metadataStore;
        _payloadStore = payloadStore;
        _ownerStateStore = ownerStateStore;
        _notificationService = notificationService;
        _realtimeNotifications = realtimeNotifications;
        _logger = logger;
    }

    [Function("UploadClipboardItem")]
    public async Task<HttpResponseData> UploadClipboardItemAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "clipboard/upload")] HttpRequestData req,
        CancellationToken cancellationToken)
    {
        var body = await ReadRequestBodyAsync(req, cancellationToken);
        var declaredLength = GetDeclaredContentLength(req);
        if (string.IsNullOrWhiteSpace(body))
        {
            const string reason = "Request body is required.";
            _logger.LogWarning(
                "Upload request without body detected; returning BadRequest (ContentLength={ContentLength}). Reason: {Reason}",
                declaredLength,
                reason);
            return await CreateBadRequestAsync(req, reason, cancellationToken);
        }

        UploadClipboardRequestDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<UploadClipboardRequestDto>(body, UploadSerializerOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Upload request JSON payload invalid");
            dto = null;
        }
        catch (NotSupportedException ex)
        {
            _logger.LogWarning(ex, "Upload request media type not supported");
            dto = null;
        }

        if (dto is null)
        {
            const string reason = "Request body JSON is invalid.";
            _logger.LogWarning(
                "Upload request body missing or invalid (BodyLength={BodyLength}, ContentLength={ContentLength}). Reason: {Reason}",
                body.Length,
                declaredLength,
                reason);
            return await CreateBadRequestAsync(req, reason, cancellationToken);
        }

        byte[] payloadBytes;
        try
        {
            payloadBytes = Convert.FromBase64String(dto.ContentBase64);
        }
        catch (FormatException ex)
        {
            const string reason = "ContentBase64 is not valid base64.";
            _logger.LogWarning(ex, "Upload request content is not valid base64");
            return await CreateBadRequestAsync(req, reason, cancellationToken);
        }

        var descriptor = new ClipboardPayloadDescriptor(
            dto.PayloadType,
            new[]
            {
                new ClipboardPayloadPart(
                    dto.FileName ?? "clipboard.bin",
                    dto.ContentType,
                    payloadBytes.LongLength,
                    () => new MemoryStream(payloadBytes, writable: false))
            },
            PreferredContentType: dto.ContentType,
            dto.ExpiresUtc
        );

        var metadata = await _coordinator.SaveAsync(
            new ClipboardUploadRequest(dto.OwnerId, descriptor, dto.DeviceName, dto.FileName),
            cancellationToken);

        await _notificationService.PublishAsync(metadata.OwnerId, metadata, cancellationToken);
        if (_realtimeNotifications.IsEnabled)
        {
            await _realtimeNotifications.PublishAsync(metadata.OwnerId, metadata, cancellationToken);
        }

        var response = req.CreateResponse(HttpStatusCode.Created);
        await response.WriteAsJsonAsync(ClipboardItemDto.FromMetadata(metadata), cancellationToken);
        return response;
    }

    [Function("ListClipboardItems")]
    public async Task<HttpResponseData> ListClipboardItemsAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "clipboard/{ownerId}")] HttpRequestData req,
        string ownerId,
        CancellationToken cancellationToken)
    {
        var take = ResolveTake(req);
        var items = await _metadataStore.ListRecentAsync(ownerId, take, cancellationToken);
        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(items.Select(metadata => ClipboardItemDto.FromMetadata(metadata)), cancellationToken);
        return response;
    }

    [Function("DownloadClipboardItem")]
    public async Task<HttpResponseData> DownloadClipboardItemAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "clipboard/{ownerId}/{itemId}")] HttpRequestData req,
        string ownerId,
        string itemId,
        CancellationToken cancellationToken)
    {
        var metadata = await _metadataStore.GetAsync(ownerId, itemId, cancellationToken);
        if (metadata is null)
        {
            _logger.LogWarning("Clipboard item not found for {OwnerId}/{ItemId}", ownerId, itemId);
            return req.CreateResponse(HttpStatusCode.NotFound);
        }

        await using var payloadStream = await _payloadStore.OpenReadAsync(metadata.BlobName, cancellationToken);
        using var buffer = new MemoryStream();
        await payloadStream.CopyToAsync(buffer, cancellationToken);
        var base64 = Convert.ToBase64String(buffer.ToArray());

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(ClipboardItemDto.FromMetadata(metadata, base64), cancellationToken);
        return response;
    }

    [Function("DeleteClipboardOwner")]
    public async Task<HttpResponseData> DeleteClipboardOwnerAsync(
        [HttpTrigger(AuthorizationLevel.Function, "delete", Route = "clipboard/{ownerId}")] HttpRequestData req,
        string ownerId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        var deleted = await _coordinator.DeleteOwnerAsync(ownerId, cancellationToken);
        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { OwnerId = ownerId, DeletedItems = deleted }, cancellationToken);
        return response;
    }

    private static int ResolveTake(HttpRequestData req)
    {
        var result = 25;
        var query = req.Url.Query;
        if (!string.IsNullOrEmpty(query))
        {
            var trimmed = query.TrimStart('?');
            var pairs = trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries);
            foreach (var pair in pairs)
            {
                var segments = pair.Split('=', 2, StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length == 2 && segments[0].Equals("take", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(Uri.UnescapeDataString(segments[1]), out var parsed))
                    {
                        result = parsed;
                        break;
                    }
                }
            }
        }

        return Math.Clamp(result, 1, 100);
    }

    [Function("GetClipboardOwnerState")]
    public async Task<HttpResponseData> GetClipboardOwnerStateAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "clipboard/owners/{ownerId}/state")] HttpRequestData req,
        string ownerId,
        CancellationToken cancellationToken)
    {
        var state = await _ownerStateStore.GetAsync(ownerId, cancellationToken);
        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(ToDto(state), cancellationToken);
        return response;
    }

    [Function("SetClipboardOwnerState")]
    public async Task<HttpResponseData> SetClipboardOwnerStateAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "clipboard/owners/{ownerId}/state")] HttpRequestData req,
        string ownerId,
        CancellationToken cancellationToken)
    {
        OwnerStateDto? dto;
        try
        {
            dto = await req.ReadFromJsonAsync<OwnerStateDto>(cancellationToken: cancellationToken);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Owner state request JSON payload invalid");
            dto = null;
        }
        catch (AggregateException ex) when (ex.InnerException is JsonException inner)
        {
            _logger.LogWarning(inner, "Owner state request JSON payload invalid");
            dto = null;
        }
        catch (NotSupportedException ex)
        {
            _logger.LogWarning(ex, "Owner state request media type not supported");
            dto = null;
        }

        if (dto is null)
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        var targetOwner = string.IsNullOrWhiteSpace(dto.OwnerId) ? ownerId : dto.OwnerId;
        if (!targetOwner.Equals(ownerId, StringComparison.OrdinalIgnoreCase))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        var state = await _ownerStateStore.SetAsync(ownerId, dto.IsPaused, cancellationToken);
        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(ToDto(state), cancellationToken);
        return response;
    }

    private static OwnerStateDto ToDto(ClipboardOwnerState state)
        => new(state.OwnerId, state.IsPaused, state.UpdatedUtc?.UtcDateTime.ToString("O"));

    private static async Task<string> ReadRequestBodyAsync(HttpRequestData req, CancellationToken cancellationToken)
    {
        if (req.Body is null)
        {
            return string.Empty;
        }

        if (req.Body.CanSeek)
        {
            req.Body.Seek(0, SeekOrigin.Begin);
        }

        using var reader = new StreamReader(req.Body, leaveOpen: true);
        var text = await reader.ReadToEndAsync(cancellationToken);

        if (req.Body.CanSeek)
        {
            req.Body.Seek(0, SeekOrigin.Begin);
        }

        return text;
    }

    private static long? GetDeclaredContentLength(HttpRequestData req)
    {
        if (req.Headers.TryGetValues("Content-Length", out var values))
        {
            foreach (var value in values)
            {
                if (long.TryParse(value, out var length))
                {
                    return length;
                }
            }
        }

        return null;
    }

    private static async Task<HttpResponseData> CreateBadRequestAsync(HttpRequestData req, string message, CancellationToken cancellationToken)
    {
        var response = req.CreateResponse(HttpStatusCode.BadRequest);
        await response.WriteAsJsonAsync(new { error = message }, cancellationToken);
        return response;
    }

    private static readonly JsonSerializerOptions UploadSerializerOptions = new(JsonSerializerDefaults.Web);
}
