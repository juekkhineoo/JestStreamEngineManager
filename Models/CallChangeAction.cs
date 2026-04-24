namespace JestStreamEngineManager.Models;

/// <summary>
/// Represents the action encoded in the NATS JetStream subject
/// (e.g. <c>VG0.create</c>, <c>VG0.update.222</c>, <c>VG0.delete.222</c>).
/// </summary>
public enum CallChangeAction
{
    /// <summary>Action could not be determined from the subject.</summary>
    Unknown,
    /// <summary>A new call was created.</summary>
    Create,
    /// <summary>An existing call was updated.</summary>
    Update,
    /// <summary>A call was deleted.</summary>
    Delete
}
