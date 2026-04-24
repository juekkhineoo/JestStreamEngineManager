using System.Text.Json.Serialization;

namespace JestStreamEngineManager.Models;

public sealed class MediaMtxPath
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("confName")]
    public string ConfName { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public MediaMtxSource? Source { get; set; }

    [JsonPropertyName("ready")]
    public bool Ready { get; set; }

    [JsonPropertyName("tracks")]
    public List<string> Tracks { get; set; } = [];

    [JsonPropertyName("readers")]
    public List<MediaMtxReader> Readers { get; set; } = [];
}

public sealed class MediaMtxSource
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;
}

public sealed class MediaMtxReader
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;
}

public sealed class MediaMtxPathsResponse
{
    [JsonPropertyName("itemCount")]
    public int ItemCount { get; set; }

    [JsonPropertyName("pageCount")]
    public int PageCount { get; set; }

    [JsonPropertyName("items")]
    public List<MediaMtxPath> Items { get; set; } = [];
}
