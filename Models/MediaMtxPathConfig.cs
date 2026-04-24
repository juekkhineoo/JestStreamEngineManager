using System.Text.Json.Serialization;

namespace JestStreamEngineManager.Models;

/// <summary>
/// Configuration request for adding a new MediaMTX path.
/// </summary>
public sealed class MediaMtxPathConfigRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("sourceFingerprint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? SourceFingerprint { get; set; }

    [JsonPropertyName("sourceOnDemand")]
    public bool SourceOnDemand { get; set; }

    [JsonPropertyName("sourceOnDemandStartTimeout")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? SourceOnDemandStartTimeout { get; set; }

    [JsonPropertyName("sourceOnDemandCloseAfter")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? SourceOnDemandCloseAfter { get; set; }

    [JsonPropertyName("maxReaders")]
    public int MaxReaders { get; set; }

    [JsonPropertyName("fallback")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? Fallback { get; set; }

    [JsonPropertyName("record")]
    public bool Record { get; set; }

    [JsonPropertyName("recordPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? RecordPath { get; set; }

    [JsonPropertyName("overridePublisher")]
    public bool OverridePublisher { get; set; }

    [JsonPropertyName("rtspTransport")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? RtspTransport { get; set; }

    [JsonPropertyName("rtspAnyPort")]
    public bool RtspAnyPort { get; set; }
}

/// <summary>
/// Error response from MediaMTX API.
/// </summary>
public sealed class MediaMtxErrorResponse
{
    [JsonPropertyName("error")]
    public string Error { get; set; } = string.Empty;
}

/// <summary>
/// Result of a MediaMTX path operation.
/// </summary>
public sealed class MediaMtxAddPathResult
{
    public bool IsSuccess { get; set; }
    public string? ErrorMessage { get; set; }
    public int StatusCode { get; set; }
}
