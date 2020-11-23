namespace GdbTracer
{
    /// <summary>
    /// Codes used for indicating a specific action by one of the peers.
    /// </summary>
    public enum CommunicationTriggerCode : byte
    {
        TargetWrite = 0x01,
        DebuggerWrite = 0x02
    }
}