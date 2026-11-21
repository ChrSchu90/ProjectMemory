namespace ProjectMemory.Routing;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Provides persistent routing state for memory projects.
/// </summary>
public interface IProjectRegistry
{
    #region Events

    #endregion

    #region Properties

    #endregion

    #region Methods

    /// <summary>
    /// Lists all registered project routings.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The registered project routings.</returns>
    Task<IReadOnlyList<ProjectRouting>> ListProjectsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all registered exact technical context bindings.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The registered context bindings.</returns>
    Task<IReadOnlyList<ContextBinding>> ListBindingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds the project route associated with a context.
    /// </summary>
    /// <param name="contextId">The context identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The persisted route, or <see langword="null"/> when no binding exists.</returns>
    Task<ProjectRoute?> FindRouteAsync(ContextId contextId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds a registered project by its immutable server-owned identifier.
    /// </summary>
    /// <param name="memoryProjectId">The immutable server-owned project identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The registered project routing, or <see langword="null"/> when no routing exists.</returns>
    Task<ProjectRouting?> FindByMemoryProjectIdAsync(Guid memoryProjectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds a registered project by its exact human-readable name.
    /// </summary>
    /// <param name="memoryProjectName">The human-readable project name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The registered project routing, or <see langword="null"/> when no routing exists.</returns>
    Task<ProjectRouting?> FindByMemoryProjectNameAsync(string memoryProjectName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a registered project routing.
    /// </summary>
    /// <param name="memoryProjectId">The immutable server-owned project identifier.</param>
    /// <param name="memoryProjectName">The human-readable project name.</param>
    /// <param name="status">The initial routing status.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The created project routing.</returns>
    Task<ProjectRouting> CreateAsync(Guid memoryProjectId, string memoryProjectName, ProjectStatus status, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to remove the registered project routing and its dependent context bindings.
    /// </summary>
    /// <param name="memoryProjectId">The immutable server-owned project identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// <see langword="true"/> when exactly one project routing was removed;
    /// otherwise <see langword="false"/>.
    /// </returns>
    Task<bool> TryRemoveProjectAsync(Guid memoryProjectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds the registered binding for an exact technical context identifier.
    /// </summary>
    /// <param name="contextId">The exact context identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The registered binding, or <see langword="null"/> when no binding exists.</returns>
    Task<ContextBinding?> FindBindingAsync(ContextId contextId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a context binding for an existing registered project routing.
    /// </summary>
    /// <param name="contextId">The exact context identifier.</param>
    /// <param name="memoryProjectId">The target server-owned project identifier.</param>
    /// <param name="status">The initial binding status.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The created binding.</returns>
    Task<ContextBinding> CreateBindingAsync(ContextId contextId, Guid memoryProjectId, Status status, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to change a project-routing status only when the persisted status still matches the expected status.
    /// </summary>
    /// <param name="memoryProjectId">The immutable server-owned project identifier.</param>
    /// <param name="expectedStatus">The status that must currently be persisted.</param>
    /// <param name="newStatus">The status to persist.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><see langword="true"/> when exactly one routing was updated; otherwise <see langword="false"/>.</returns>
    Task<bool> TryUpdateProjectStatusAsync(Guid memoryProjectId, ProjectStatus expectedStatus, ProjectStatus newStatus, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to change a context-binding status only when the persisted status still matches the expected status.
    /// </summary>
    /// <param name="contextId">The exact technical context identifier.</param>
    /// <param name="expectedStatus">The status that must currently be persisted.</param>
    /// <param name="newStatus">The status to persist.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><see langword="true"/> when exactly one binding was updated; otherwise <see langword="false"/>.</returns>
    Task<bool> TryUpdateBindingStatusAsync(ContextId contextId, Status expectedStatus, Status newStatus, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to remove the exact registered context binding.
    /// </summary>
    /// <param name="contextId">The exact technical context identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><see langword="true"/> when exactly one binding was removed; otherwise <see langword="false"/>.</returns>
    Task<bool> TryRemoveBindingAsync(ContextId contextId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists a stable bounded page of projects.
    /// </summary>
    /// <param name="offset">The number of projects to skip in the stable name and identifier ordering.</param>
    /// <param name="limit">The maximum number of projects to return; must be positive.</param>
    /// <param name="cancellationToken">The token used to cancel the database query.</param>
    /// <returns>A task containing the requested page of active and archived projects.</returns>
    Task<IReadOnlyList<ProjectRouting>> ListProjectsPageAsync(int offset, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists a stable bounded page of bindings, optionally limited to one explicit project.
    /// </summary>
    /// <param name="projectId">The project to filter by, or <see langword="null"/> to include every project.</param>
    /// <param name="offset">The number of bindings to skip in the stable type, exact name and row identifier ordering.</param>
    /// <param name="limit">The maximum number of bindings to return; must be positive.</param>
    /// <param name="cancellationToken">The token used to cancel the database query.</param>
    /// <returns>A task containing the requested exact bindings, including preserved inactive bindings.</returns>
    Task<IReadOnlyList<ContextBinding>> ListBindingsPageAsync(Guid? projectId, int offset, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically checks project availability and creates or confirms an exact active binding.
    /// </summary>
    /// <param name="contextId">The validated, case-sensitive context key to bind or confirm.</param>
    /// <param name="projectId">The immutable identifier of the active target project.</param>
    /// <param name="cancellationToken">The token used to cancel the transactional operation.</param>
    /// <returns>A task containing the binding snapshot and change flag, or a missing-project, archived-project or binding-conflict error.</returns>
    Task<RegistryChange<ContextBinding>> BindAsync(ContextId contextId, Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically sets project status while preserving IDs and bindings.
    /// </summary>
    /// <param name="projectId">The immutable identifier of the project to archive or reactivate.</param>
    /// <param name="status">The requested active or archived project status.</param>
    /// <param name="cancellationToken">The token used to cancel the transactional operation.</param>
    /// <returns>A task containing the project snapshot and change flag, or a missing-project error.</returns>
    Task<RegistryChange<ProjectRouting>> SetProjectStatusAsync(Guid projectId, ProjectStatus status, CancellationToken cancellationToken = default);

    #endregion
}
