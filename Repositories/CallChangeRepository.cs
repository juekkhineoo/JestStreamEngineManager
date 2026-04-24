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
/// The stream must already exist; this consumer uses an ephemeral pull consumer starting from the
/// sequence after <paramref name="lastSequence"/> so that each polling cycle picks up exactly the
/// messages that arrived since the previous cycle.
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
            var consumerConfig = new ConsumerConfig
            {
                DeliverPolicy = lastSequence == 0 ? ConsumerConfigDeliverPolicy.LastPerSubject : ConsumerConfigDeliverPolicy.ByStartSequence,
                AckPolicy = ConsumerConfigAckPolicy.Explicit,
                // FilterSubject intentionally omitted — the stream is already scoped to calls.>
                // and the > wildcard is illegal in the consumer create API subject.
                InactiveThreshold = TimeSpan.FromSeconds(60),
                AckWait = TimeSpan.FromSeconds(30),
                MaxDeliver = 5
            };

            if (lastSequence > 0)
            {
                consumerConfig.OptStartSeq = lastSequence + 1;
            }

            var consumer = await _jetStream!.CreateOrUpdateConsumerAsync(
                _settings.JetStreamName, consumerConfig, cancellationToken);

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
            // Stream does not exist yet — non-fatal, return empty result and wait for next cycle.
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
}
