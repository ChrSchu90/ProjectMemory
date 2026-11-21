namespace ProjectMemory.Routing;

/// <summary>
/// Retains legacy context-binding state; project lifecycle uses <see cref="ProjectStatus"/>.
/// </summary>
public enum Status
{
    /// <summary>
    /// The binding is active and can be used.
    /// </summary>
    Active,

    /// <summary>
    /// The binding is inactive and must not resolve a project.
    /// </summary>
    Inactive
}
