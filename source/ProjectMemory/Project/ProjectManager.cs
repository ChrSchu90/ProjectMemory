namespace ProjectMemory.Project;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ProjectMemory.Routing;
using ProjectMemory.Tools;

/// <summary>
/// Owns validated project administration without client paths, implicit binding or project fallback.
/// </summary>
/// <param name="registry">The existing SQLite registry boundary.</param>
/// <param name="routing">The existing fail-closed route resolver.</param>
internal sealed class ProjectManager(IProjectRegistry registry, RoutingManager routing)
{
    #region Static Fields

    #endregion

    #region Private Fields

    #endregion

    #region Constructors

    #endregion

    #region Properties

    #endregion

    #region Public Methods

    /// <summary>
    /// Creates an active project with a fresh server-issued immutable ID.
    /// </summary>
    /// <param name="name">The human-readable name to validate and register without deriving a storage path.</param>
    /// <param name="cancellationToken">The token used to cancel project creation.</param>
    /// <returns>A task containing the new project or an argument-validation or duplicate-name error.</returns>
    public async Task<AdministrationResponse<ProjectResult>> CreateProjectAsync(string? name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name.Any(char.IsControl))
            return AdministrationResponse<ProjectResult>.Failure("INVALID_ARGUMENT");
        try
        {
            var project = await registry.CreateAsync(Guid.NewGuid(), name, ProjectStatus.Active, cancellationToken).ConfigureAwait(false);
            return AdministrationResponse<ProjectResult>.Success(new ProjectResult(Describe(project)));
        }
        catch (ProjectRegistryConflictException)
        {
            return AdministrationResponse<ProjectResult>.Failure("ALREADY_EXISTS");
        }
    }

    /// <summary>
    /// Lists projects, including archived projects, using stable bounded pagination.
    /// </summary>
    /// <param name="offset">The non-negative number of projects to skip.</param>
    /// <param name="limit">The requested page size, from 1 through 200.</param>
    /// <param name="cancellationToken">The token used to cancel the project query.</param>
    /// <returns>A task containing the project page and pagination metadata, or an invalid-argument error.</returns>
    public async Task<AdministrationResponse<ProjectListResult>> ListProjectsAsync(int offset = 0, int limit = 50, CancellationToken cancellationToken = default)
    {
        if (!ValidPage(offset, limit))
            return AdministrationResponse<ProjectListResult>.Failure("INVALID_ARGUMENT");
        var rows = await registry.ListProjectsPageAsync(offset, limit + 1, cancellationToken).ConfigureAwait(false);
        return AdministrationResponse<ProjectListResult>.Success(new ProjectListResult(rows.Take(limit).Select(Describe).ToArray(), offset, limit, rows.Count > limit));
    }

    /// <summary>
    /// Lists exact bindings globally or for an explicitly selected project, including archived projects.
    /// </summary>
    /// <param name="projectId">The project UUID in hyphenated form, or <see langword="null"/> to list bindings across projects.</param>
    /// <param name="offset">The non-negative number of bindings to skip.</param>
    /// <param name="limit">The requested page size, from 1 through 200.</param>
    /// <param name="cancellationToken">The token used to cancel project validation and the binding query.</param>
    /// <returns>A task containing the binding page and pagination metadata, or an invalid-argument or missing-project error.</returns>
    public async Task<AdministrationResponse<ContextBindingListResult>> ListBindingsAsync(string? projectId = null, int offset = 0, int limit = 50, CancellationToken cancellationToken = default)
    {
        if (!ValidPage(offset, limit))
            return AdministrationResponse<ContextBindingListResult>.Failure("INVALID_ARGUMENT");
        Guid? selected = null;
        if (projectId is not null)
        {
            if (!ValidProjectId(projectId, out var id))
                return AdministrationResponse<ContextBindingListResult>.Failure("INVALID_ARGUMENT");
            if (await registry.FindByMemoryProjectIdAsync(id, cancellationToken).ConfigureAwait(false) is null)
                return AdministrationResponse<ContextBindingListResult>.Failure("PROJECT_NOT_FOUND");
            selected = id;
        }

        var rows = await registry.ListBindingsPageAsync(selected, offset, limit + 1, cancellationToken).ConfigureAwait(false);
        return AdministrationResponse<ContextBindingListResult>.Success(new ContextBindingListResult(rows.Take(limit).Select(binding => new ContextBindingDetails(binding.ContextId.ToString(), binding.MemoryProjectId, binding.Status == Status.Active)).ToArray(), offset, limit, rows.Count > limit));
    }

    /// <summary>
    /// Archives or reactivates a project idempotently without modifying its bindings.
    /// </summary>
    /// <param name="projectId">The immutable project UUID in hyphenated form.</param>
    /// <param name="status">The active or archived project status requested by the administration tool.</param>
    /// <param name="cancellationToken">The token used to cancel the transactional status change.</param>
    /// <returns>A task containing the project and change flag, or an invalid-argument or missing-project error.</returns>
    public async Task<AdministrationResponse<ProjectStatusResult>> SetProjectStatusAsync(string? projectId, ProjectStatus status, CancellationToken cancellationToken = default)
    {
        if (!ValidProjectId(projectId, out var id))
            return AdministrationResponse<ProjectStatusResult>.Failure("INVALID_ARGUMENT");
        var change = await registry.SetProjectStatusAsync(id, status, cancellationToken).ConfigureAwait(false);
        return change.Error is { } error ? Failure<ProjectStatusResult>(error) : AdministrationResponse<ProjectStatusResult>.Success(new ProjectStatusResult(Describe(change.Value!), change.Changed));
    }

    /// <summary>
    /// Creates or confirms an exact active binding without silently changing its owner.
    /// </summary>
    /// <param name="context">The exact case-sensitive context ID with a supported namespace.</param>
    /// <param name="projectId">The immutable UUID of the target project in hyphenated form.</param>
    /// <param name="cancellationToken">The token used to cancel validation and the binding transaction.</param>
    /// <returns>A task containing the binding and change flag, or a validation, project-availability or binding-conflict error.</returns>
    public async Task<AdministrationResponse<ContextBindingResult>> BindAsync(string? context, string? projectId, CancellationToken cancellationToken = default)
    {
        if (!ContextId.TryParse(context, out var contextId))
            return AdministrationResponse<ContextBindingResult>.Failure("INVALID_CONTEXT");
        if (!ValidProjectId(projectId, out var id))
            return AdministrationResponse<ContextBindingResult>.Failure("INVALID_ARGUMENT");
        var change = await registry.BindAsync(contextId, id, cancellationToken).ConfigureAwait(false);
        return change.Error is { } error ? Failure<ContextBindingResult>(error) : AdministrationResponse<ContextBindingResult>.Success(new ContextBindingResult(contextId.ToString(), id, change.Changed));
    }

    /// <summary>
    /// Removes only the explicitly named binding; an unknown context is an error without side effects.
    /// </summary>
    /// <param name="context">The exact case-sensitive context ID whose binding should be removed.</param>
    /// <param name="cancellationToken">The token used to cancel the binding removal.</param>
    /// <returns>A task confirming removal of the binding, or an invalid-context or unbound-context error.</returns>
    public async Task<AdministrationResponse<ContextUnbindingResult>> UnbindAsync(string? context, CancellationToken cancellationToken = default)
    {
        if (!ContextId.TryParse(context, out var contextId))
            return AdministrationResponse<ContextUnbindingResult>.Failure("INVALID_CONTEXT");
        var removed = await registry.TryRemoveBindingAsync(contextId, cancellationToken).ConfigureAwait(false);
        return removed ? AdministrationResponse<ContextUnbindingResult>.Success(new ContextUnbindingResult(contextId.ToString(), true)) : AdministrationResponse<ContextUnbindingResult>.Failure("CONTEXT_UNBOUND");
    }

    /// <summary>
    /// Resolves only the exact active project/binding pair, with no normalization or fallback.
    /// </summary>
    /// <param name="context">The exact case-sensitive context ID to resolve without normalization.</param>
    /// <param name="cancellationToken">The token used to cancel the persisted route lookup.</param>
    /// <returns>A task containing the resolved project identity, or an invalid-context, unbound-context or archived-project error.</returns>
    public async Task<AdministrationResponse<ContextResolutionResult>> ResolveAsync(string? context, CancellationToken cancellationToken = default)
    {
        if (!ContextId.TryParse(context, out var contextId))
            return AdministrationResponse<ContextResolutionResult>.Failure("INVALID_CONTEXT");
        var result = await routing.ResolveContextAsync(contextId, cancellationToken).ConfigureAwait(false);
        return result.Status switch
        {
            ContextResolutionStatus.Resolved => AdministrationResponse<ContextResolutionResult>.Success(new ContextResolutionResult(contextId.ToString(), result.MemoryProjectId!.Value, result.MemoryProjectName!)),
            ContextResolutionStatus.ProjectRoutingInactive => AdministrationResponse<ContextResolutionResult>.Failure("PROJECT_ARCHIVED"),
            _ => AdministrationResponse<ContextResolutionResult>.Failure("CONTEXT_UNBOUND")
        };
    }

    #endregion

    #region Private Methods

    private static bool ValidProjectId(string? value, out Guid id) => Guid.TryParseExact(value, "D", out id) && id != Guid.Empty;

    private static bool ValidPage(int offset, int limit) => offset >= 0 && limit is >= 1 and <= 200;

    private static ProjectDetails Describe(ProjectRouting project) => new(project.MemoryProjectId, project.MemoryProjectName, project.Status == ProjectStatus.Active ? "active" : "archived");

    private static AdministrationResponse<TData> Failure<TData>(RegistryError error) where TData : class => AdministrationResponse<TData>.Failure(error switch
    {
        RegistryError.ProjectNotFound => "PROJECT_NOT_FOUND",
        RegistryError.ProjectArchived => "PROJECT_ARCHIVED",
        _ => "CONFLICT"
    });

    #endregion
}
