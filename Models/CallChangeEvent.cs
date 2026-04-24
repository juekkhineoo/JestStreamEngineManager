namespace JestStreamEngineManager.Models;

/// <summary>
/// Represents a call change event consumed from NATS JetStream.
/// </summary>
public class CallChangeEvent
{
    /// <summary>
    /// The sequence number of the JetStream message (used as the "version").
    /// </summary>
    public ulong Sequence { get; set; }

    /// <summary>
    /// The unique identifier of the call that changed.
    /// </summary>
    public long CallId { get; set; }

    /// <summary>
    /// The current status of the call.
    /// </summary>
    public CallStatus? Status { get; set; }

    /// <summary>
    /// The base URL for playback streams from the associated recorder.
    /// Mapped from the <c>playbackStreamUrlBase</c> field in the NATS payload.
    /// </summary>
    public string? PlaybackStreamUrlBase { get; set; }

    /// <summary>
    /// The raw NATS subject the message was published to (e.g. <c>calls.111</c>).
    /// Populated by the repository after the message is consumed.
    /// </summary>
    public string? Subject { get; set; }

    /// <summary>
    /// The action derived from the payload's <c>action</c> field (create / update / delete).
    /// </summary>
    public CallChangeAction Action { get; set; }
}
