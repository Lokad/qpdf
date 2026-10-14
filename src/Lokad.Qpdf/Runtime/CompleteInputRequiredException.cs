namespace Lokad.Qpdf.Runtime;

// Internal control flow for a bounded speculative read. It must not be caught
// as PDF damage: the selection owner retries once with the same complete input.
internal sealed class CompleteInputRequiredException : Exception { }
