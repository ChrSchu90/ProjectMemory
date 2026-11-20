namespace ProjectMemory.Routing;

/// <summary>
/// Identifies the source type of project context binding.
/// </summary>
public enum BindingType
{
    /// <summary>
    /// A ChatGPT project context using the <c>chatgpt-project</c> identifier.
    /// </summary>
    ChatProject,

    /// <summary>
    /// A Git repository context using the <c>git</c> identifier.
    /// </summary>
    GitRepository,

    /// <summary>
    /// A local Git repository context.
    /// </summary>
    //LocalRepository // ToDo: do we need this?
}
