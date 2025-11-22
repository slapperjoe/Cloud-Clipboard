using System;

namespace CloudClipboard.Core.Models;

public sealed record OwnerConfiguration(string OwnerId, string ConfigurationJson, DateTimeOffset UpdatedUtc);
