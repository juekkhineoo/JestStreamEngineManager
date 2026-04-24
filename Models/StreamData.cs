namespace JestStreamEngineManager.Models;

/// <summary>
/// Represents an active call with essential streaming information.
/// </summary>
public class StreamData
{
    public long CallId { get; set; }
    public string? PlaybackStreamUrlBase { get; set; }

    public StreamData(long callId, string? playbackStreamUrlBase)
    {
        CallId = callId;
        PlaybackStreamUrlBase = playbackStreamUrlBase;
    }

    public override bool Equals(object? obj) =>
        obj is StreamData other && CallId == other.CallId;

    public override int GetHashCode() => CallId.GetHashCode();
}
