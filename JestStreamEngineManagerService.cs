using JestStreamEngineManager.Configuration;
using JestStreamEngineManager.Models;
using JestStreamEngineManager.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JestStreamEngineManager;

/// <summary>
/// Background service that manages MediaMTX path configuration based on call change events
/// consumed from NATS JetStream.
/// </summary>
public sealed class JestStreamEngineManagerService(
    IServiceProvider serviceProvider,
    IOptions<JestStreamEngineManagerSettings> options,
    ILogger<JestStreamEngineManagerService> logger) : BackgroundService
{
    private readonly IServiceProvider _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    private readonly JestStreamEngineManagerSettings _settings = options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly ILogger<JestStreamEngineManagerService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly CancellationTokenSource _cts = new();

    // JetStream sequence cursor — 0 means "start from the beginning".
    private ulong _lastSequence = 0;
    private IMediaMtxRepository _mediaMtxRepository = null!;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "JestStreamEngineManager started. Polling interval: {Interval}s, Stream: {Stream}, Subject: {Subject}",
            _settings.PollingIntervalSeconds, _settings.JetStreamName, _settings.JetStreamSubject);

        var timer = new PeriodicTimer(TimeSpan.FromSeconds(_settings.PollingIntervalSeconds));
        var consecutiveErrors = 0;
        const int maxConsecutiveErrors = 5;

        try
        {
            await UpdatePathsAsync(stoppingToken);
            consecutiveErrors = 0;

            while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await UpdatePathsAsync(stoppingToken);
                    consecutiveErrors = 0;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    consecutiveErrors++;
                    _logger.LogError(ex, "Error during path update (consecutive errors: {Count})", consecutiveErrors);

                    if (consecutiveErrors >= maxConsecutiveErrors)
                    {
                        var delaySeconds = Math.Min(300, (int)Math.Pow(2, Math.Min(consecutiveErrors - maxConsecutiveErrors, 8)));
                        _logger.LogWarning("Backing off for {Delay}s after {Errors} consecutive errors",
                            delaySeconds, consecutiveErrors);

                        try { await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken); }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Service execution cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Fatal error — service will terminate");
            throw;
        }
        finally
        {
            timer.Dispose();
            _logger.LogInformation("Service execution completed");
        }
    }

    /// <summary>
    /// One update cycle: fetch new JetStream events then reconcile MediaMTX paths.
    /// <para>
    /// Add/remove decisions are driven by both the subject action and call status:
    /// <list type="bullet">
    ///   <item><term>create + active status</term><description>Add stream</description></item>
    ///   <item><term>update + active status</term><description>Add stream</description></item>
    ///   <item><term>update + non-active status</term><description>Remove stream</description></item>
    ///   <item><term>delete</term><description>Remove stream (regardless of status)</description></item>
    /// </list>
    /// </para>
    /// </summary>
    public async Task UpdatePathsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Starting update cycle. LastSequence: {Seq}", _lastSequence);

            await using var scope = _serviceProvider.CreateAsyncScope();
            var changeRepo = scope.ServiceProvider.GetRequiredService<ICallChangeRepository>();
            _mediaMtxRepository = scope.ServiceProvider.GetRequiredService<IMediaMtxRepository>();

            var result = await changeRepo.GetCallChangesSinceAsync(_lastSequence, cancellationToken);

            if (result is null)
            {
                _logger.LogWarning("Null result returned from CallChangeRepository");
                return;
            }

            _lastSequence = result.LastSequence;

            var existingPaths = await _mediaMtxRepository.GetActivePathsAsync();
            _logger.LogDebug("Current MediaMTX paths: [{Paths}]", string.Join(", ", existingPaths));

            if (result.Changes is null || result.Changes.Count == 0)
            {
                _logger.LogInformation("No call change events since sequence {Seq}", _lastSequence);
                return;
            }

            // Add: create or update events where the call is active.
            var addCalls = result.Changes
                .Where(c => c.Action !=  CallChangeAction.Delete
                            && c.Status == CallStatus.InProgress)
                .Select(c => new StreamData(c.CallId, c.PlaybackStreamUrlBase + c.CallId))
                .ToList();

            // Remove: any event where the call is no longer active, regardless of action.
            var removeCalls = result.Changes
                .Where(c => c.Action == CallChangeAction.Delete || c.Status != CallStatus.InProgress)
                .Select(c => new StreamData(c.CallId, null))
                .ToList();

            if (removeCalls.Count > 0)
                await DeletePathsAsync(removeCalls, existingPaths);

            if (addCalls.Count > 0)
                await AddPathsAsync(addCalls, existingPaths);

            if (addCalls.Count == 0 && removeCalls.Count == 0)
                _logger.LogInformation("No MediaMTX changes required");
            else
                _logger.LogInformation("MediaMTX configuration updated successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during path update");
            throw;
        }
    }

    private MediaMtxPathConfigRequest CreatePathConfigFromCall(StreamData call)
    {
        ArgumentNullException.ThrowIfNull(call);

        if (call.CallId <= 0)
            throw new ArgumentException("CallId must be > 0", nameof(call));

        if (string.IsNullOrWhiteSpace(call.PlaybackStreamUrlBase))
            throw new ArgumentException("PlaybackStreamUrlBase cannot be empty", nameof(call));

        return new MediaMtxPathConfigRequest
        {
            Name = call.CallId.ToString(),
            Source = call.PlaybackStreamUrlBase,
            SourceOnDemand = false,
            Record = false
        };
    }

    private async Task AddPathsAsync(List<StreamData> addCalls, List<string> existingPaths)
    {
        var pathsToAdd = addCalls
            .Where(c => c.CallId > 0 && !string.IsNullOrWhiteSpace(c.PlaybackStreamUrlBase))
            .Where(c => !existingPaths.Contains(c.CallId.ToString()))
            .ToList();

        if (pathsToAdd.Count == 0)
        {
            _logger.LogInformation("No new paths to add — all active calls already present in MediaMTX");
            return;
        }

        _logger.LogInformation("Adding {Count} new path(s): [{Ids}]",
            pathsToAdd.Count, string.Join(", ", pathsToAdd.Select(c => c.CallId)));

        var addTasks = pathsToAdd.Select(async call =>
        {
            var cfg = CreatePathConfigFromCall(call);
            return await _mediaMtxRepository.AddPathAsync(call.CallId.ToString(), cfg);
        });

        var results = await Task.WhenAll(addTasks);
        var failed = results.Where(r => !r.IsSuccess).ToList();
        if (failed.Count > 0)
        {
            _logger.LogWarning("Failed to add {Failed}/{Total} paths", failed.Count, pathsToAdd.Count);
            foreach (var f in failed)
                _logger.LogWarning("  Error: {Msg}", f.ErrorMessage);
        }
        else
        {
            _logger.LogInformation("Successfully added {Count} path(s)", pathsToAdd.Count);
        }
    }

    private async Task DeletePathsAsync(List<StreamData> deletedCalls, List<string> existingPaths)
    {
        var toRemove = deletedCalls
            .Where(c => c.CallId > 0)
            .Select(c => c.CallId.ToString())
            .Where(existingPaths.Contains)
            .ToList();

        if (toRemove.Count == 0)
        {
            _logger.LogDebug("No paths to remove for deleted calls");
            return;
        }

        await DeletePathsByCallIds(toRemove);
    }

    private async Task DeletePathsByCallIds(List<string> pathsToRemove)
    {
        _logger.LogInformation("Removing {Count} path(s) from MediaMTX: [{Paths}]",
            pathsToRemove.Count, string.Join(", ", pathsToRemove));

        var results = await Task.WhenAll(
            pathsToRemove.Select(id => _mediaMtxRepository.RemovePathAsync(id)));

        var failed = results.Where(r => !r.IsSuccess).ToList();
        if (failed.Count > 0)
        {
            _logger.LogWarning("Failed to remove {Failed}/{Total} paths",
                failed.Count, pathsToRemove.Count);
            foreach (var f in failed)
                _logger.LogWarning("  Error: {Msg}", f.ErrorMessage);
        }
        else
        {
            _logger.LogInformation("Successfully removed {Count} path(s)", pathsToRemove.Count);
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _cts.Dispose();
        base.Dispose();
    }
}
