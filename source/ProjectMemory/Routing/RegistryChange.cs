namespace ProjectMemory.Routing;

/// <summary>
/// Returns a transaction's snapshot or an expected failure without leaking storage exceptions.
/// </summary>
/// <typeparam name="T">The changed registry record.</typeparam>
/// <param name="Value">The record as observed by the transaction.</param>
/// <param name="Changed">Whether persistent state changed.</param>
/// <param name="Error">An expected failure, if any.</param>
public sealed record RegistryChange<T>(T? Value, bool Changed = false, RegistryError? Error = null) where T : class;
