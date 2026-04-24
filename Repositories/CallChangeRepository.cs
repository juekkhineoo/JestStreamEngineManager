using JestStreamEngineManager.Configuration;
using JestStreamEngineManager.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System.Text.RegularExpressions;

namespace JestStreamEngineManager.Repositories;

/// <summary>
/// Reads call change events from a NATS JetStream stream.
/// Each message on the stream carries a <see cref="CallChangeEvent"/> as its JSON payload.
/// <para>
/// Delivery strategy depends on <c>lastSequence</c> passed to <see cref="GetCallChangesSinceAsync"/>:
/// <list type="bullet">
///   <item>
///     <term><c>lastSequence == 0</c></term>
///     <description>
///       Uses a durable pull consumer with <see cref="ConsumerConfigDeliverPolicy.LastPerSubject"/>
///       and <see cref="ConsumerConfigAckPolicy.None"/> to obtain the current state of every
///       call without replaying the entire stream history.
///     </description>
///   </item>
///   <item>
///     <term><c>lastSequence != 0</c></term>
///     <description>
///       Creates an ephemeral consumer with <see cref="ConsumerConfigDeliverPolicy.ByStartSequence"/>
///       starting at <c>lastSequence + 1</c> so only genuinely new messages are returned.
///     </description>
///   </item>
/// </list>
/// </para>
/// </summary>
public sealed class CallChangeRepository : ICallChangeRepository, IAsyncDisposable, IDisposable
{
    private readonly JestStreamEngineManagerSettings _settings;
    private readonly ILogger<CallChangeRepository> _logger;
    private NatsConnection? _natsConnection;
    private NatsJSContext? _jetStream;
    private bool _initialized;
    private bool _disposed;

    private static readonly JsonSerializerSettings JsonOpts = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        Converters = { new Newtonsoft.Json.Converters.StringEnumConverter() }
    };

    // Quotes unquoted object keys:  foo:  →  "foo":
    private static readonly Regex UnquotedKeyRegex =
        new(@"(?<=[{,])\s*([A-Za-z_]\w*)\s*:", RegexOptions.Compiled);

    // Quotes unquoted string values (skips numbers, booleans, null, and already-quoted values):
    //   : InProgress          →  : "InProgress"
    //   : rtsp://host/path/   →  : "rtsp://host/path/"
    private static readonly Regex UnquotedValueRegex =
        new(@":\s*([A-Za-z][^,}]*)", RegexOptions.Compiled);

    public CallChangeRepository(
        IOptions<JestStreamEngineManagerSettings> options,
        ILogger<CallChangeRepository> logger)
    {
        _settings = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<CallChangeResult> GetCallChangesSinceAsync(
        ulong lastSequence,
        CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);

        var result = new CallChangeResult
        {
            Changes = [],
            LastSequence = lastSequence
        };

        try
        {
            var consumer = await GetConsumerAsync(lastSequence, cancellationToken);

            var fetchOpts = new NatsJSFetchOpts
            {
                MaxMsgs = 1000,
                Expires = TimeSpan.FromSeconds(5)
            };

            await foreach (var msg in consumer.FetchAsync<string>(opts: fetchOpts, cancellationToken: cancellationToken))
            {
                if (msg.Metadata.HasValue)
                    result.LastSequence = Math.Max(result.LastSequence, msg.Metadata.Value.Sequence.Stream);

                if (msg.Data is null) continue;

                try
                {
                    var normalizedData = NormalizeLooseJson(msg.Data);
                    var changeEvent = JsonConvert.DeserializeObject<CallChangeEvent>(normalizedData, JsonOpts);
                    if (changeEvent is not null)
                    {
                        if (msg.Metadata.HasValue)
                            changeEvent.Sequence = msg.Metadata.Value.Sequence.Stream;

                        // Subject format: calls.<callId> — action is always in the payload.
                        changeEvent.Subject = msg.Subject;

                        result.Changes.Add(changeEvent);
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex,
                        "Skipping unparseable JetStream message at sequence {Seq}. Payload: {Payload}",
                        msg.Metadata?.Sequence.Stream, msg.Data);
                }
            }

            _logger.LogInformation(
                "Fetched {Count} call change event(s) from JetStream. LastSequence: {LastSeq}",
                result.Changes.Count, result.LastSequence);
        }
        catch (NatsJSApiException ex) when (ex.Error.Code == 404)
        {
            // Stream or consumer does not exist yet — non-fatal, return empty result.
            _logger.LogWarning("JetStream stream '{Stream}' not found. Returning empty result.", _settings.JetStreamName);
            return result;
        }
        catch (NatsJSException ex)
        {
            // Transient JetStream error — reset connection so the next poll cycle reconnects.
            _logger.LogError(ex, "JetStream error fetching call changes. Resetting connection.");
            await ResetConnectionAsync();
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Unexpected error fetching call changes from JetStream");
            throw;
        }

        return result;
    }

    /// <summary>
    /// Returns the appropriate JetStream consumer based on <paramref name="lastSequence"/>:
    /// <list type="bullet">
    ///   <item>
    ///     <term><c>0</c></term>
    ///     <description>
    ///       Reuses or creates the durable consumer with
    ///       <see cref="ConsumerConfigDeliverPolicy.LastPerSubject"/> so the first cycle
    ///       captures the current state of every call without a full history replay.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <term>non-zero</term>
    ///     <description>
    ///       Creates an ephemeral consumer with
    ///       <see cref="ConsumerConfigDeliverPolicy.ByStartSequence"/> at
    ///       <c>lastSequence + 1</c> so only new messages are delivered.
    ///     </description>
    ///   </item>
    /// </list>
    /// </summary>
    private Task<INatsJSConsumer> GetConsumerAsync(ulong lastSequence, CancellationToken cancellationToken) =>
        lastSequence == 0
            ? GetOrCreateDurableConsumerAsync(cancellationToken)
            : CreateEphemeralConsumerAsync(lastSequence, cancellationToken);

    /// <summary>
    /// Returns the existing durable consumer if present on the NATS server, or creates it
    /// with <see cref="ConsumerConfigDeliverPolicy.LastPerSubject"/>.
    /// <para>
    /// <see cref="ConsumerConfigAckPolicy.None"/> is used so NATS advances the server-side
    /// cursor automatically on delivery — no explicit <c>AckAsync</c> call is required.
    /// </para>
    /// </summary>
    private async Task<INatsJSConsumer> GetOrCreateDurableConsumerAsync(CancellationToken cancellationToken)
    {
        // Attempt to bind to the existing durable consumer first.
        try
        {
            var existing = await _jetStream!.GetConsumerAsync(
                _settings.JetStreamName, _settings.JetStreamConsumerName, cancellationToken);

            _logger.LogDebug(
                "Reusing existing durable consumer '{Consumer}' on stream '{Stream}'",
                _settings.JetStreamConsumerName, _settings.JetStreamName);

            return existing;
        }
        catch (NatsJSApiException ex) when (ex.Error.Code == 404)
        {
            // Consumer does not exist yet — fall through to create it.
            _logger.LogInformation(
                "Durable consumer '{Consumer}' not found on stream '{Stream}'. Creating it now.",
                _settings.JetStreamConsumerName, _settings.JetStreamName);
        }

        var filterSubject = BuildConsumerFilterSubject(_settings.JetStreamSubject);

        var consumerConfig = new ConsumerConfig
        {
            Name = _settings.JetStreamConsumerName,
            // Start from the last message per subject so the first cycle picks up the
            // current state of every call rather than replaying the entire stream history.
            DeliverPolicy = ConsumerConfigDeliverPolicy.LastPerSubject,
            // AckPolicy.None lets NATS advance the cursor automatically on delivery,
            // removing the need for explicit AckAsync calls in the fetch loop.
            AckPolicy = ConsumerConfigAckPolicy.None,
            // Required by NATS when DeliverPolicy is LastPerSubject.
            FilterSubject = filterSubject,
            InactiveThreshold = TimeSpan.FromSeconds(60),
        };

        _logger.LogDebug(
            "Creating durable consumer '{Consumer}' on stream '{Stream}' with filter '{Filter}'",
            _settings.JetStreamConsumerName, _settings.JetStreamName, filterSubject);

        return await _jetStream!.CreateOrUpdateConsumerAsync(
            _settings.JetStreamName, consumerConfig, cancellationToken);
    }

    /// <summary>
    /// Creates a short-lived ephemeral consumer that starts at <paramref name="lastSequence"/> + 1,
    /// delivering only messages published after the last successfully processed sequence.
    /// <para>
    /// <c>FilterSubject</c> is intentionally omitted. The NATS 2.10+ consumer API encodes
    /// the filter into the request subject (<c>$JS.API.CONSUMER.CREATE.&lt;stream&gt;.&lt;filter&gt;</c>),
    /// but the .NET client only constructs that subject when a <c>Name</c> is provided.
    /// For unnamed (ephemeral) consumers the client sends to the unfiltered endpoint, and the
    /// server rejects any mismatch between the request subject and the config body.
    /// Omitting the filter is safe: the stream itself is already scoped to
    /// <see cref="JestStreamEngineManagerSettings.JetStreamSubject"/> so no foreign messages exist.
    /// </para>
    /// </summary>
    private async Task<INatsJSConsumer> CreateEphemeralConsumerAsync(
        ulong lastSequence,
        CancellationToken cancellationToken)
    {
        var consumerConfig = new ConsumerConfig
        {
            // No Name — ephemeral consumer; NATS cleans it up automatically after InactiveThreshold.
            DeliverPolicy = ConsumerConfigDeliverPolicy.ByStartSequence,
            OptStartSeq = lastSequence + 1,
            AckPolicy = ConsumerConfigAckPolicy.None,
            // FilterSubject deliberately omitted — see summary above.
            InactiveThreshold = TimeSpan.FromSeconds(30),
        };

        _logger.LogDebug(
            "Creating ephemeral consumer on stream '{Stream}' starting at sequence {Seq}",
            _settings.JetStreamName, lastSequence + 1);

        return await _jetStream!.CreateOrUpdateConsumerAsync(
            _settings.JetStreamName, consumerConfig, cancellationToken);
    }

    private async Task ResetConnectionAsync()
    {
        _initialized = false;
        _jetStream = null;

        if (_natsConnection is not null)
        {
            await _natsConnection.DisposeAsync();
            _natsConnection = null;
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;

        _logger.LogInformation("Connecting to NATS at {Url}", _settings.NatsUrl);

        try
        {
            var opts = new NatsOpts
            {
                Url = _settings.NatsUrl,
                Name = "JestStreamEngineManager",
                ReconnectWaitMin = TimeSpan.FromSeconds(1),
                ReconnectWaitMax = TimeSpan.FromSeconds(10)
            };

            _natsConnection = new NatsConnection(opts);
            await _natsConnection.ConnectAsync();

            _jetStream = new NatsJSContext(_natsConnection);

            await EnsureStreamAsync(cancellationToken);

            _initialized = true;

            _logger.LogInformation("Connected to NATS JetStream successfully");
        }
        catch (Exception ex)
        {
            // Reset so the next poll cycle retries the connection from scratch.
            _initialized = false;
            _jetStream = null;

            if (_natsConnection is not null)
            {
                await _natsConnection.DisposeAsync();
                _natsConnection = null;
            }

            _logger.LogError(ex, "Failed to connect to NATS at {Url}", _settings.NatsUrl);
            throw;
        }
    }

    /// <summary>
    /// Creates or updates the JetStream stream so it captures all subjects defined by
    /// <see cref="JestStreamEngineManagerSettings.JetStreamSubject"/> (e.g. <c>calls.&gt;</c>).
    /// Without this, a stream provisioned with only <c>calls.create</c> will silently drop
    /// <c>calls.update.*</c> and <c>calls.delete.*</c> messages.
    /// </summary>
    private async Task EnsureStreamAsync(CancellationToken cancellationToken)
    {
        var streamConfig = new StreamConfig(_settings.JetStreamName, [_settings.JetStreamSubject])
        {
            Storage = StreamConfigStorage.File,
            Retention = StreamConfigRetention.Limits,
        };

        try
        {
            await _jetStream!.CreateStreamAsync(streamConfig, cancellationToken);
            _logger.LogInformation(
                "JetStream stream '{Stream}' created with subject filter '{Subject}'",
                _settings.JetStreamName, _settings.JetStreamSubject);
        }
        catch (NatsJSApiException ex) when (ex.Error.Code == 400 || ex.Error.Code == 409)
        {
            // Stream already exists — update it to ensure the subject filter is correct.
            await _jetStream!.UpdateStreamAsync(streamConfig, cancellationToken);
            _logger.LogInformation(
                "JetStream stream '{Stream}' updated with subject filter '{Subject}'",
                _settings.JetStreamName, _settings.JetStreamSubject);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_natsConnection is not null)
        {
            await _natsConnection.DisposeAsync();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _natsConnection?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Normalizes loose JSON (unquoted keys / string values) into valid JSON so that
    /// Newtonsoft can deserialize payloads produced by non-strict publishers.
    /// Example: <c>{callId:22222,status:InProgress}</c> → <c>{"callId":22222,"status":"InProgress"}</c>
    /// </summary>
    private static string NormalizeLooseJson(string json)
    {
        json = UnquotedKeyRegex.Replace(json, "\"$1\":");
        json = UnquotedValueRegex.Replace(json, m => $": \"{m.Groups[1].Value.Trim()}\"");
        return json;
    }

    /// <summary>
    /// Converts a stream-level subject wildcard into a consumer-safe filter subject.
    /// NATS consumers do not accept the multi-level <c>&gt;</c> wildcard in <c>FilterSubject</c>;
    /// the single-level <c>*</c> wildcard is supported and covers the typical
    /// <c>calls.&gt;</c> → <c>calls.*</c> pattern used here.
    /// </summary>
    /// <param name="streamSubject">The subject pattern from settings, e.g. <c>calls.&gt;</c>.</param>
    /// <returns>A consumer-compatible filter subject, e.g. <c>calls.*</c>.</returns>
    private static string BuildConsumerFilterSubject(string streamSubject)
    {
        // Replace a trailing ".>" with ".*" to produce a valid consumer filter subject.
        if (streamSubject.EndsWith(".>", StringComparison.Ordinal))
            return string.Concat(streamSubject.AsSpan(0, streamSubject.Length - 1), "*");

        // Bare ">" means match everything — use "*" for a single-level equivalent.
        if (streamSubject == ">")
            return "*";

        // Already a concrete subject or uses "*" — use as-is.
        return streamSubject;
    }
}
