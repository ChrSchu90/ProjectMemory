namespace ProjectMemory.Routing;

/// <summary>
/// Identifies the source type of project context binding.
/// </summary>
public enum BindingType
{
    /// <summary>
    /// A vendor-neutral web context using the <c>chat</c> prefix.
    /// </summary>
    ChatProject,

    /// <summary>
    /// A Git repository context using the <c>git</c> identifier.
    /// </summary>
    GitRepository,

    /// <summary>
    /// A local Git repository context.
    /// </summary>
    LocalRepository
}
