using System;
using CloudClipboard.Core.Models;

namespace CloudClipboard.Functions.Dtos;

public sealed record OwnerConfigurationDto(string OwnerId, string ConfigurationJson, DateTimeOffset UpdatedUtc)
{
    public static OwnerConfigurationDto FromModel(OwnerConfiguration model)
        => new(model.OwnerId, model.ConfigurationJson, model.UpdatedUtc);
}

public sealed record OwnerConfigurationRequest(string? ConfigurationJson);
