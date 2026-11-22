namespace ProjectMemory.Tests.Project;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjectMemory.Project;
using ProjectMemory.Routing;

/// <summary>
/// Verifies exact, fail-closed context resolution through <see cref="ProjectManager"/>.
/// </summary>
[TestClass]
public sealed class ProjectManagerResolutionTests
{
    #region Static Fields

    private const string TestContext = "git:github.com/ChrSchu90/ProjectMemory";

    #endregion

    #region Private Fields

    #endregion

    #region Test Constructors

    #endregion

    #region Test Preparation

    #endregion

    #region Tests

    /// <summary>
    /// Verifies that an unknown context cannot resolve a project.
    /// </summary>
    /// <returns>A task that completes after checking the context-resolution behavior.</returns>
    [TestMethod]
    public async Task ResolveAsyncReturnsContextUnboundWhenRouteDoesNotExist()
    {
        var manager = CreateManager(null);

        var result = await manager.ResolveAsync(TestContext);

        Assert.IsFalse(result.Ok);
        Assert.AreEqual("CONTEXT_UNBOUND", result.Error!.Code);
        Assert.IsNull(result.Data);
    }

    /// <summary>
    /// Verifies that malformed context identifiers fail before the registry is queried.
    /// </summary>
    /// <returns>A task that completes after checking the context-resolution behavior.</returns>
    [TestMethod]
    public async Task ResolveAsyncRejectsInvalidContext()
    {
        var manager = CreateManager(null);

        var result = await manager.ResolveAsync("git:");

        Assert.IsFalse(result.Ok);
        Assert.AreEqual("INVALID_CONTEXT", result.Error!.Code);
    }

    /// <summary>
    /// Verifies that an archived project cannot be reached through an active binding.
    /// </summary>
    /// <returns>A task that completes after checking the context-resolution behavior.</returns>
    [TestMethod]
    public async Task ResolveAsyncRejectsArchivedProject()
    {
        var manager = CreateManager(new ProjectRoute(Guid.NewGuid(), "project", ProjectStatus.Archived, Status.Active));

        var result = await manager.ResolveAsync(TestContext);

        Assert.IsFalse(result.Ok);
        Assert.AreEqual("PROJECT_ARCHIVED", result.Error!.Code);
        Assert.IsNull(result.Data);
    }

    /// <summary>
    /// Verifies that an inactive binding cannot expose an active project.
    /// </summary>
    /// <returns>A task that completes after checking the context-resolution behavior.</returns>
    [TestMethod]
    public async Task ResolveAsyncRejectsInactiveBinding()
    {
        var manager = CreateManager(new ProjectRoute(Guid.NewGuid(), "project", ProjectStatus.Active, Status.Inactive));

        var result = await manager.ResolveAsync(TestContext);

        Assert.IsFalse(result.Ok);
        Assert.AreEqual("CONTEXT_UNBOUND", result.Error!.Code);
        Assert.IsNull(result.Data);
    }

    /// <summary>
    /// Verifies that an active route resolves to the exact registered project identity.
    /// </summary>
    /// <returns>A task that completes after checking the context-resolution behavior.</returns>
    [TestMethod]
    public async Task ResolveAsyncReturnsActiveProject()
    {
        var id = Guid.NewGuid();
        var manager = CreateManager(new ProjectRoute(id, "project", ProjectStatus.Active, Status.Active));

        var result = await manager.ResolveAsync(TestContext);

        Assert.IsTrue(result.Ok);
        Assert.AreEqual(TestContext, result.Data!.ContextId);
        Assert.AreEqual(id, result.Data.ProjectId);
        Assert.AreEqual("project", result.Data.ProjectName);
    }

    /// <summary>
    /// Verifies that cancelled route lookups propagate cancellation without producing a fallback result.
    /// </summary>
    /// <returns>A task that completes after checking the context-resolution behavior.</returns>
    [TestMethod]
    public async Task ResolveAsyncPropagatesCancellation()
    {
        var registry = new StubProjectRegistry(static (_, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<ProjectRoute?>(null);
        });
        var manager = new ProjectManager(registry, new ProjectLocks());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => manager.ResolveAsync(TestContext, cancellation.Token));
    }

    /// <summary>
    /// Verifies that an unsupported persisted project status cannot authorize a context.
    /// </summary>
    /// <returns>A task that completes after checking the context-resolution behavior.</returns>
    [TestMethod]
    public async Task ResolveAsyncRejectsUnknownProjectStatus()
    {
        var manager = CreateManager(new ProjectRoute(Guid.NewGuid(), "project", (ProjectStatus)int.MaxValue, Status.Active));

        var result = await manager.ResolveAsync(TestContext);

        Assert.AreEqual("PROJECT_ARCHIVED", result.Error!.Code);
    }

    /// <summary>
    /// Verifies that an unsupported persisted binding status cannot authorize a context.
    /// </summary>
    /// <returns>A task that completes after checking the context-resolution behavior.</returns>
    [TestMethod]
    public async Task ResolveAsyncRejectsUnknownBindingStatus()
    {
        var manager = CreateManager(new ProjectRoute(Guid.NewGuid(), "project", ProjectStatus.Active, (Status)int.MaxValue));

        var result = await manager.ResolveAsync(TestContext);

        Assert.AreEqual("CONTEXT_UNBOUND", result.Error!.Code);
    }

    #endregion

    #region Private Methods

    private static ProjectManager CreateManager(ProjectRoute? route) => new(new StubProjectRegistry((_, _) => Task.FromResult<ProjectRoute?>(route)), new ProjectLocks());

    #endregion

    #region Test Classes

    private sealed class StubProjectRegistry(Func<ContextId, CancellationToken, Task<ProjectRoute?>> findRoute) : IProjectRegistry
    {
        #region Static Fields

        #endregion

        #region Private Fields

        #endregion

        #region Test Constructors

        #endregion

        #region Test Preparation

        #endregion

        #region Tests

        /// <inheritdoc />
        public Task<ProjectRoute?> FindRouteAsync(ContextId contextId, CancellationToken cancellationToken = default) => findRoute(contextId, cancellationToken);

        /// <inheritdoc />
        public Task<ProjectRouting?> FindByMemoryProjectIdAsync(Guid memoryProjectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        /// <inheritdoc />
        public Task<ProjectRouting> CreateAsync(Guid memoryProjectId, string memoryProjectName, ProjectStatus status, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        /// <inheritdoc />
        public Task<ContextBinding?> FindBindingAsync(ContextId contextId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        /// <inheritdoc />
        public Task<bool> TryRemoveBindingAsync(ContextId contextId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        /// <inheritdoc />
        public Task<IReadOnlyList<ProjectRouting>> ListProjectsPageAsync(int offset, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        /// <inheritdoc />
        public Task<IReadOnlyList<ContextBinding>> ListBindingsPageAsync(Guid? projectId, int offset, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        /// <inheritdoc />
        public Task<RegistryChange<ContextBinding>> BindAsync(ContextId contextId, Guid projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        /// <inheritdoc />
        public Task<RegistryChange<ProjectRouting>> SetProjectStatusAsync(Guid projectId, ProjectStatus status, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        #endregion

        #region Private Methods

        #endregion

        #region Test Classes

        #endregion
    }

    #endregion
}
