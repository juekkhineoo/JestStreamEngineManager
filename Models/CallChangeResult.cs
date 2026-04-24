namespace JestStreamEngineManager.Models;

/// <summary>
/// Wraps the list of call change events fetched from JetStream plus the last consumed sequence number.
/// </summary>
public class CallChangeResult
{
    /// <summary>
    /// The call change events retrieved in this batch.
    /// </summary>
    public List<CallChangeEvent>? Changes { get; set; }

    /// <summary>
    /// The last JetStream sequence number successfully consumed.
    /// </summary>
    public ulong LastSequence { get; set; }
}
