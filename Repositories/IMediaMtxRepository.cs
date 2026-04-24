using JestStreamEngineManager.Models;

namespace JestStreamEngineManager.Repositories;

/// <summary>
/// Repository interface for MediaMTX API operations.
/// </summary>
public interface IMediaMtxRepository
{
    /// <summary>Gets the active streaming paths from MediaMTX.</summary>
    Task<List<string>> GetActivePathsAsync();

    /// <summary>Adds a new MediaMTX path with the specified configuration.</summary>
    Task<MediaMtxAddPathResult> AddPathAsync(string callId, MediaMtxPathConfigRequest pathConfig);

    /// <summary>Removes a MediaMTX path by call ID.</summary>
    Task<MediaMtxAddPathResult> RemovePathAsync(string callId);
}
