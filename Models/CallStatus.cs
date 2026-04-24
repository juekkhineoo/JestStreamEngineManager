namespace JestStreamEngineManager.Models;

/// <summary>
/// Represents the current status of a televisit call.
/// </summary>
public enum CallStatus
{
    /// <summary>No specific status or uninitialized state.</summary>
    None,
    /// <summary>Call is ready to be initiated.</summary>
    Ready,
    /// <summary>Call is currently being connected/dialed.</summary>
    Dialing,
    /// <summary>Call is active and in progress.</summary>
    InProgress,
    /// <summary>Call is temporarily paused.</summary>
    Paused,
    /// <summary>Call has been completed successfully.</summary>
    Completed,
    /// <summary>Call failed to connect or was terminated unexpectedly.</summary>
    Failed,
    /// <summary>An error occurred during the call.</summary>
    Error
}
