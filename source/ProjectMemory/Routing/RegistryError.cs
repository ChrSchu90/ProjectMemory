namespace ProjectMemory.Routing;

/// <summary>
/// Describes expected transactional administration failures.
/// </summary>
public enum RegistryError
{
    /// <summary>
    /// There is no explicitly selected project.
    /// </summary>
    ProjectNotFound,

    /// <summary>
    /// The selected project cannot accept bindings.
    /// </summary>
    ProjectArchived,

    /// <summary>
    /// A different or inactive binding already owns the context.
    /// </summary>
    Conflict
}
