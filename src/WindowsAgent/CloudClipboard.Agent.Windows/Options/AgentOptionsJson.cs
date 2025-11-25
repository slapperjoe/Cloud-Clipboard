using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudClipboard.Agent.Windows.Options;

internal static class AgentOptionsJson
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static AgentOptions? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<AgentOptions>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string Serialize(AgentOptions options)
        => JsonSerializer.Serialize(options, SerializerOptions);

    public static AgentOptions Clone(AgentOptions source)
    {
        var json = Serialize(source);
        return JsonSerializer.Deserialize<AgentOptions>(json, SerializerOptions)!;
    }

    public static void Normalize(AgentOptions options)
    {
        options.PinnedItems ??= new();
        options.FunctionsDeployment ??= FunctionsDeploymentOptions.CreateDefault();
        var deploymentDefaults = FunctionsDeploymentOptions.CreateDefault();
        if (string.IsNullOrWhiteSpace(options.FunctionsDeployment.PackagePath))
        {
            options.FunctionsDeployment.PackagePath = deploymentDefaults.PackagePath;
        }

        if (string.IsNullOrWhiteSpace(options.FunctionsDeployment.Location))
        {
            options.FunctionsDeployment.Location = deploymentDefaults.Location;
        }

        if (string.IsNullOrWhiteSpace(options.FunctionsDeployment.PayloadContainer))
        {
            options.FunctionsDeployment.PayloadContainer = deploymentDefaults.PayloadContainer;
        }

        if (string.IsNullOrWhiteSpace(options.FunctionsDeployment.MetadataTable))
        {
            options.FunctionsDeployment.MetadataTable = deploymentDefaults.MetadataTable;
        }
    }

    public static bool AreEquivalent(AgentOptions left, AgentOptions right)
        => string.Equals(Serialize(left), Serialize(right), StringComparison.Ordinal);
}
