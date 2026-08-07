namespace McenterLite.Shared.Ipc
{
    /// <summary>What a <see cref="PipeEnvelope"/> is asking for, or reporting.</summary>
    public enum Command
    {
        /// <summary>Widget -> helper. Read a value. Answered with <see cref="Response"/>.</summary>
        Get = 0,

        /// <summary>
        /// Widget -> helper. Write a value. Always answered with a <see cref="Response"/> carrying
        /// the value the hardware ACTUALLY ended up at, which may differ from the request after
        /// clamping. The widget renders that, never its own optimistic value.
        /// </summary>
        Set = 1,

        /// <summary>Helper -> widget. Reply to a Get or Set, correlated by <c>Id</c>.</summary>
        Response = 2,

        /// <summary>Helper -> widget. Unsolicited push (telemetry, or state changed behind our back). Id is 0.</summary>
        Event = 3,

        /// <summary>Helper -> widget. The request failed; <c>Error</c> carries a human-readable reason.</summary>
        Error = 4,
    }
}
