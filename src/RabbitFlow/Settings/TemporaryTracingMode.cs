namespace EasyRabbitFlow.Settings
{
    /// <summary>
    /// How the per-message span of a temporary run relates to the trace context that was ambient
    /// when <c>RunAsync</c> was called.
    /// </summary>
    public enum TemporaryTracingMode
    {
        /// <summary>
        /// Each message span is a child of the context captured when <c>RunAsync</c> was called, so the whole
        /// run belongs to the caller's trace. Suits small runs that the caller awaits.
        /// </summary>
        Child = 0,

        /// <summary>
        /// Each message span starts a new trace and carries a link to the context captured when <c>RunAsync</c>
        /// was called. Every message becomes a small, complete, independently sampled trace, which suits large
        /// batches, long runs and fire-and-forget fan-out, where the caller's span ends long before the run does.
        /// The handler is detached from the caller's ambient activity even when nothing listens to the
        /// EasyRabbitFlow source.
        /// </summary>
        LinkedRoot = 1
    }
}
