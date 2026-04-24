using JestStreamEngineManager.Models;

namespace JestStreamEngineManager.Repositories;

/// <summary>
/// Repository interface for reading call change events from NATS JetStream.
/// </summary>
public interface ICallChangeRepository
{
    /// <summary>
    /// Fetches all pending call change events from JetStream since the last consumed sequence.
    /// </summary>
    /// <param name="lastSequence">The last JetStream sequence number already processed. Use 0 for initial load.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="CallChangeResult"/> containing the batch of changes and the new last sequence.</returns>
    Task<CallChangeResult> GetCallChangesSinceAsync(ulong lastSequence, CancellationToken cancellationToken = default);
}
