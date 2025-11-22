using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CloudClipboard.Core.Models;
using CloudClipboard.Functions.Dtos;
using CloudClipboard.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudClipboard.Functions.Functions;

public sealed class OwnerConfigurationFunctions
{
    private readonly IOwnerConfigurationStore _configurationStore;
    private readonly ILogger<OwnerConfigurationFunctions> _logger;

    public OwnerConfigurationFunctions(IOwnerConfigurationStore configurationStore, ILogger<OwnerConfigurationFunctions> logger)
    {
        _configurationStore = configurationStore;
        _logger = logger;
    }

    [Function("GetOwnerConfiguration")]
    public async Task<HttpResponseData> GetAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "clipboard/owners/{ownerId}/configuration")] HttpRequestData req,
        string ownerId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        var configuration = await _configurationStore.GetAsync(ownerId, cancellationToken).ConfigureAwait(false);
        if (configuration is null)
        {
            return req.CreateResponse(HttpStatusCode.NotFound);
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(OwnerConfigurationDto.FromModel(configuration), cancellationToken);
        return response;
    }

    [Function("SetOwnerConfiguration")]
    public async Task<HttpResponseData> SetAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "clipboard/owners/{ownerId}/configuration")] HttpRequestData req,
        string ownerId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        OwnerConfigurationRequest? dto;
        try
        {
            dto = await req.ReadFromJsonAsync<OwnerConfigurationRequest>(cancellationToken: cancellationToken);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Owner configuration payload invalid for {OwnerId}", ownerId);
            dto = null;
        }

        if (dto is null || string.IsNullOrWhiteSpace(dto.ConfigurationJson))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("ConfigurationJson is required.", cancellationToken);
            return badRequest;
        }

        var configuration = await _configurationStore.SetAsync(ownerId, dto.ConfigurationJson, cancellationToken).ConfigureAwait(false);
        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(OwnerConfigurationDto.FromModel(configuration), cancellationToken);
        return response;
    }
}
