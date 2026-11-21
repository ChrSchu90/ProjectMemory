namespace ProjectMemory.Routing;

/// <summary>
/// Defines the lifecycle of a registered project independently of legacy binding state.
/// </summary>
public enum ProjectStatus
{
    /// <summary>
    /// The project can be resolved and accept new bindings.
    /// </summary>
    Active,

    /// <summary>
    /// The project and bindings are preserved but ordinary access is blocked.
    /// </summary>
    Archived
}
