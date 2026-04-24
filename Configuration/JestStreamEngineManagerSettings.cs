using System.ComponentModel.DataAnnotations;

namespace JestStreamEngineManager.Configuration;

/// <summary>
/// Configuration settings for the JestStream Engine Manager application.
/// </summary>
public sealed class JestStreamEngineManagerSettings
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "JestStreamEngineManager";

    /// <summary>
    /// Gets or sets the polling interval in seconds.
    /// </summary>
    [Range(5, 100)]
    public int PollingIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Gets or sets the HTTP request timeout in seconds.
    /// </summary>
    [Range(5, 300)]
    public int HttpTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Gets or sets the MediaMTX API base URL for direct API calls.
    /// </summary>
    public string MediaMtxApiUrl { get; set; } = "http://localhost:9997";

    /// <summary>
    /// Gets or sets the NATS server URL.
    /// </summary>
    public string NatsUrl { get; set; } = "nats://localhost:4222";

    /// <summary>
    /// Gets or sets the JetStream stream name that carries call change events.
    /// </summary>
    public string JetStreamName { get; set; } = "CALL_CHANGES";

    /// <summary>
    /// Gets or sets the JetStream subject for call change events.
    /// </summary>
    public string JetStreamSubject { get; set; } = "calls.changes";

    /// <summary>
    /// Gets or sets the durable consumer name used to track delivery progress.
    /// </summary>
    public string JetStreamConsumerName { get; set; } = "jest-stream-engine-manager";
}
