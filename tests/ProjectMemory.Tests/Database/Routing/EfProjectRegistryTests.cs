namespace ProjectMemory.Tests.Database.Routing;

using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjectMemory.Database.Entities;
using ProjectMemory.Database.Routing;
using ProjectMemory.Database;
using ProjectMemory.Project;
using ProjectMemory.Routing;
using ProjectMemory.Tests.Database;

/// <summary>
/// Tests for <see cref="EfProjectRegistry"/>
/// </summary>
[TestClass]
public sealed class EfProjectRegistryTests
{
    #region Static Fields

    #endregion

    #region Private Fields

    private string _databasePath = null!;
    private string _testRoot = null!;

    #endregion

    #region Test Constructors

    #endregion

    #region Test Preparation

    /// <summary>
    /// Prepares a unique disposable data directory and database path for the test.
    /// </summary>
    [TestInitialize]
    public void Initialize()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "ProjectMemory.Tests", Path.GetRandomFileName());
        _databasePath = Path.Combine(_testRoot, $"{Path.GetRandomFileName()}.db");
        Directory.CreateDirectory(_testRoot);
    }

    /// <summary>
    /// Removes the test's isolated directory and any remaining SQLite files.
    /// </summary>
    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    #endregion

    #region Tests

    /// <summary>
    /// Verifies that an exact context binding returns its project identifier and persisted statuses.
    /// </summary>
    /// <returns>A task that completes when the test has verified that an exact context binding returns its project identifier and persisted statuses.</returns>
    [TestMethod]
    public async Task FindRouteAsyncReturnsExactRoute()
    {
        var memoryProjectId = Guid.NewGuid();
        var memoryProjectName = Guid.NewGuid().ToString("N");
        await CreateDatabaseAsync(_databasePath,
            new ProjectRoutingEntity
            {
                MemoryProjectId = memoryProjectId,
                MemoryProjectName = memoryProjectName,
                Status = ProjectStatus.Active,
                Bindings =
                {
                    new ContextBindingEntity
                    {
                        BindingType = BindingType.GitRepository,
                        BindingName = "github.com/ChrSchu90/ProjectMemory",
                        Status = Status.Active
                    }
                }
            });

        var registry = CreateRegistry(_databasePath);
        Assert.IsTrue(ContextId.TryParse("git:github.com/ChrSchu90/ProjectMemory", out var contextId));

        var route = await registry.FindRouteAsync(contextId!, CancellationToken.None);
        Assert.IsNotNull(route);
        Assert.AreEqual(memoryProjectId, route.MemoryProjectId);
        Assert.AreEqual(memoryProjectName, route.MemoryProjectName);
        Assert.AreEqual(ProjectStatus.Active, route.ProjectRoutingStatus);
        Assert.AreEqual(Status.Active, route.BindingStatus);
    }

    /// <summary>
    /// Verifies that an unknown context returns no route.
    /// </summary>
    /// <returns>A task that completes when the test has verified that an unknown context returns no route.</returns>
    [TestMethod]
    public async Task FindRouteAsyncReturnsNullForUnknownContext()
    {
        await CreateDatabaseAsync(_databasePath,
            new ProjectRoutingEntity
            {
                MemoryProjectId = Guid.NewGuid(),
                MemoryProjectName = Guid.NewGuid().ToString("N"),
                Bindings =
                {
                    new ContextBindingEntity
                    {
                        BindingType = BindingType.GitRepository,
                        BindingName = "github.com/ChrSchu90/ProjectMemory"
                    }
                }
            });

        var registry = CreateRegistry(_databasePath);
        Assert.IsTrue(ContextId.TryParse("git:github.com/ChrSchu90/OtherRepository", out var contextId));

        var route = await registry.FindRouteAsync(contextId!, CancellationToken.None);
        Assert.IsNull(route);
    }

    /// <summary>
    /// Verifies that inactive binding state is returned to the core layer instead of being filtered by persistence.
    /// </summary>
    /// <returns>A task that completes when the test has verified that inactive binding state is returned to the core layer instead of being filtered by persistence.</returns>
    [TestMethod]
    public async Task FindRouteAsyncReturnsInactiveBindingStatus()
    {
        await CreateDatabaseAsync(_databasePath,
            new ProjectRoutingEntity
            {
                MemoryProjectId = Guid.NewGuid(),
                MemoryProjectName = Guid.NewGuid().ToString("N"),
                Status = ProjectStatus.Active,
                Bindings =
                {
                    new ContextBindingEntity
                    {
                        BindingType = BindingType.ChatProject,
                        BindingName = "chatty-mcp-and-aiharborvm",
                        Status = Status.Inactive
                    }
                }
            });

        var registry = CreateRegistry(_databasePath);
        Assert.IsTrue(ContextId.TryParse("chat:chatty-mcp-and-aiharborvm", out var contextId));

        var route = await registry.FindRouteAsync(contextId!, CancellationToken.None);
        Assert.IsNotNull(route);
        Assert.AreEqual(ProjectStatus.Active, route.ProjectRoutingStatus);
        Assert.AreEqual(Status.Inactive, route.BindingStatus);
    }

    /// <summary>
    /// Verifies that inactive project routing state is returned to the core layer instead of being filtered by persistence.
    /// </summary>
    /// <returns>A task that completes when the test has verified that inactive project routing state is returned to the core layer instead of being filtered by persistence.</returns>
    [TestMethod]
    public async Task FindRouteAsyncReturnsInactiveProjectRoutingStatus()
    {
        await CreateDatabaseAsync(_databasePath,
            new ProjectRoutingEntity
            {
                MemoryProjectId = Guid.NewGuid(),
                MemoryProjectName = Guid.NewGuid().ToString("N"),
                Status = ProjectStatus.Archived,
                Bindings =
                {
                    new ContextBindingEntity
                    {
                        BindingType = BindingType.GitRepository,
                        BindingName = "github.com/ChrSchu90/ProjectMemory",
                        Status = Status.Active
                    }
                }
            });

        var registry = CreateRegistry(_databasePath);
        Assert.IsTrue(ContextId.TryParse("git:github.com/ChrSchu90/ProjectMemory", out var contextId));

        var route = await registry.FindRouteAsync(contextId!, CancellationToken.None);
        Assert.IsNotNull(route);
        Assert.AreEqual(ProjectStatus.Archived, route.ProjectRoutingStatus);
        Assert.AreEqual(Status.Active, route.BindingStatus);
    }

    /// <summary>
    /// Verifies that the database prevents the same context from being bound to more than one project routing.
    /// </summary>
    /// <returns>A task that completes when the test has verified that the database prevents the same context from being bound to more than one project routing.</returns>
    [TestMethod]
    public async Task DatabaseRejectsDuplicateContextBinding()
    {
        var options = ContextOptions.Create(_databasePath, pooling: false);

        await using var dbContext = new ProjectMemoryDbContext(options);
        await dbContext.Database.MigrateAsync();

        dbContext.RoutingProjects.AddRange(
            new ProjectRoutingEntity
            {
                MemoryProjectId = Guid.NewGuid(),
                MemoryProjectName = "project-a",
                Bindings =
                {
                    new ContextBindingEntity
                    {
                        BindingType = BindingType.GitRepository,
                        BindingName = "github.com/ChrSchu90/ProjectMemory"
                    }
                }
            },
            new ProjectRoutingEntity
            {
                MemoryProjectId = Guid.NewGuid(),
                MemoryProjectName = "project-b",
                Bindings =
                {
                    new ContextBindingEntity
                    {
                        BindingType = BindingType.GitRepository,
                        BindingName = "github.com/ChrSchu90/ProjectMemory"
                    }
                }
            });

        await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());
    }

    /// <summary>
    /// Verifies that the database prevents two local project routings from using the same project name.
    /// </summary>
    /// <returns>A task that completes when the test has verified that the database prevents two local project routings from using the same project name.</returns>
    [TestMethod]
    public async Task DatabaseRejectsDuplicateMemoryProjectName()
    {
        var options = ContextOptions.Create(_databasePath, pooling: false);

        await using var dbContext = new ProjectMemoryDbContext(options);
        await dbContext.Database.MigrateAsync();

        dbContext.RoutingProjects.AddRange(
            new ProjectRoutingEntity
            {
                MemoryProjectId = Guid.NewGuid(),
                MemoryProjectName = "shared-project-name"
            },
            new ProjectRoutingEntity
            {
                MemoryProjectId = Guid.NewGuid(),
                MemoryProjectName = "shared-project-name"
            });

        await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());
    }

    /// <summary>
    /// Verifies that a registered project routing can be found by its external identifier.
    /// </summary>
    /// <returns>A task that completes when the test has verified that a registered project routing can be found by its external identifier.</returns>
    [TestMethod]
    public async Task FindByMemoryProjectIdAsyncReturnsRouting()
    {
        var memoryProjectId = Guid.NewGuid();
        var memoryProjectName = "project-a";
        await CreateDatabaseAsync(_databasePath, new ProjectRoutingEntity
        {
            MemoryProjectId = memoryProjectId,
            MemoryProjectName = memoryProjectName,
            Status = ProjectStatus.Archived
        });

        var registry = CreateRegistry(_databasePath);
        var routing = await registry.FindByMemoryProjectIdAsync(memoryProjectId, CancellationToken.None);
        Assert.IsNotNull(routing);
        Assert.AreEqual(memoryProjectId, routing.MemoryProjectId);
        Assert.AreEqual(memoryProjectName, routing.MemoryProjectName);
        Assert.AreEqual(ProjectStatus.Archived, routing.Status);
    }

    /// <summary>
    /// Verifies that an unregistered project identifier cannot be resolved.
    /// </summary>
    /// <returns>A task that completes after checking the registry operation.</returns>
    [TestMethod]
    public async Task FindByMemoryProjectIdReturnsNullWhenRoutingDoesNotExist()
    {
        await CreateDatabaseAsync(_databasePath, new ProjectRoutingEntity
        {
            MemoryProjectId = Guid.NewGuid(),
            MemoryProjectName = "project-a"
        });

        var registry = CreateRegistry(_databasePath);
        Assert.IsNull(await registry.FindByMemoryProjectIdAsync(Guid.NewGuid(), CancellationToken.None));
    }

    /// <summary>
    /// Verifies that creating a project routing persists its identity and requested initial status.
    /// </summary>
    /// <returns>A task that completes when the test has verified that creating a project routing persists its identity and requested initial status.</returns>
    [TestMethod]
    public async Task CreateAsyncPersistsProjectRouting()
    {
        var options = ContextOptions.Create(_databasePath, pooling: false);
        await using (var dbContext = new ProjectMemoryDbContext(options))
        {
            await dbContext.Database.MigrateAsync();
        }

        var memoryProjectId = Guid.NewGuid();
        var memoryProjectName = "project-a";
        var registry = CreateRegistry(_databasePath);

        var created = await registry.CreateAsync(memoryProjectId, memoryProjectName, ProjectStatus.Archived, CancellationToken.None);
        Assert.AreEqual(memoryProjectId, created.MemoryProjectId);
        Assert.AreEqual(memoryProjectName, created.MemoryProjectName);
        Assert.AreEqual(ProjectStatus.Archived, created.Status);

        var persisted = await registry.FindByMemoryProjectIdAsync(memoryProjectId, CancellationToken.None);
        Assert.IsNotNull(persisted);
        Assert.AreEqual(created, persisted);
    }

    /// <summary>
    /// Verifies that creating a project routing rejects unsupported status values before modifying persistence.
    /// </summary>
    /// <returns>A task that completes when the test has verified that creating a project routing rejects unsupported status values before modifying persistence.</returns>
    [TestMethod]
    public async Task CreateAsyncRejectsUnsupportedStatus()
    {
        var options = ContextOptions.Create(_databasePath, pooling: false);
        await using (var dbContext = new ProjectMemoryDbContext(options))
        {
            await dbContext.Database.MigrateAsync();
        }

        var registry = CreateRegistry(_databasePath);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => registry.CreateAsync(Guid.NewGuid(), "project-a", (ProjectStatus)int.MaxValue, CancellationToken.None));
    }

    /// <summary>
    /// Verifies that paged binding listings preserve exact IDs, targets, statuses and stable ordering.
    /// </summary>
    /// <returns>A task that completes after checking the registry operation.</returns>
    [TestMethod]
    public async Task ListBindingsPageAsyncReturnsBindingsInStableOrder()
    {
        var chatProjectId = Guid.NewGuid();
        var gitProjectId = Guid.NewGuid();
        await CreateDatabaseAsync(_databasePath,
            new ProjectRoutingEntity
            {
                MemoryProjectId = chatProjectId,
                MemoryProjectName = "chat-project",
                Bindings = { new ContextBindingEntity { BindingType = BindingType.ChatProject, BindingName = "Portal", Status = Status.Inactive } }
            },
            new ProjectRoutingEntity
            {
                MemoryProjectId = gitProjectId,
                MemoryProjectName = "git-project",
                Bindings = { new ContextBindingEntity { BindingType = BindingType.GitRepository, BindingName = "github.com/Org/Repo", Status = Status.Active } }
            });

        var registry = CreateRegistry(_databasePath);
        var bindings = await registry.ListBindingsPageAsync(null, 0, 10, CancellationToken.None);
        Assert.HasCount(2, bindings);
        Assert.AreEqual("chat:Portal", bindings[0].ContextId.ToString());
        Assert.AreEqual(chatProjectId, bindings[0].MemoryProjectId);
        Assert.AreEqual(Status.Inactive, bindings[0].Status);
        Assert.AreEqual("git:github.com/Org/Repo", bindings[1].ContextId.ToString());
        Assert.AreEqual(gitProjectId, bindings[1].MemoryProjectId);
        Assert.AreEqual(Status.Active, bindings[1].Status);

        Assert.AreEqual(bindings[1], (await registry.ListBindingsPageAsync(null, 1, 1, CancellationToken.None)).Single());
        Assert.AreEqual(bindings[0], (await registry.ListBindingsPageAsync(chatProjectId, 0, 10, CancellationToken.None)).Single());
    }

    /// <summary>
    /// Verifies that an exact registered context binding can be found with its target project identifier and persisted status.
    /// </summary>
    /// <returns>A task that completes when the test has verified that an exact registered context binding can be found with its target project identifier and persisted status.</returns>
    [TestMethod]
    public async Task FindBindingAsyncReturnsExactBinding()
    {
        var memoryProjectId = Guid.NewGuid();
        await CreateDatabaseAsync(_databasePath, new ProjectRoutingEntity
        {
            MemoryProjectId = memoryProjectId,
            MemoryProjectName = "project-a",
            Bindings =
                {
                    new ContextBindingEntity
                        {
                            BindingType = BindingType.GitRepository,
                            BindingName = "github.com/ChrSchu90/ProjectMemory",
                            Status = Status.Inactive
                        }
                }
        });

        var registry = CreateRegistry(_databasePath);
        Assert.IsTrue(ContextId.TryParse("git:github.com/ChrSchu90/ProjectMemory", out var contextId));

        var binding = await registry.FindBindingAsync(contextId!, CancellationToken.None);
        Assert.IsNotNull(binding);
        Assert.AreEqual(contextId, binding.ContextId);
        Assert.AreEqual(memoryProjectId, binding.MemoryProjectId);
        Assert.AreEqual(Status.Inactive, binding.Status);
    }

    /// <summary>
    /// Verifies that binding lookup returns no result when the exact technical context is not registered.
    /// </summary>
    /// <returns>A task that completes when the test has verified that binding lookup returns no result when the exact technical context is not registered.</returns>
    [TestMethod]
    public async Task FindBindingAsyncReturnsNullForUnknownContext()
    {
        await CreateDatabaseAsync(_databasePath,
            new ProjectRoutingEntity
            {
                MemoryProjectId = Guid.NewGuid(),
                MemoryProjectName = "project-a",
                Bindings =
                {
                    new ContextBindingEntity
                    {
                        BindingType = BindingType.GitRepository,
                        BindingName = "github.com/ChrSchu90/ProjectMemory"
                    }
                }
            });

        var registry = CreateRegistry(_databasePath);
        Assert.IsTrue(ContextId.TryParse("git:github.com/ChrSchu90/OtherRepository", out var contextId));

        var binding = await registry.FindBindingAsync(contextId!, CancellationToken.None);
        Assert.IsNull(binding);
    }

    /// <summary>
    /// Verifies that a transactional bind creates an active binding and subsequent binds confirm it without mutation.
    /// </summary>
    /// <returns>A task that completes after checking the registry operation.</returns>
    [TestMethod]
    public async Task BindAsyncPersistsAndConfirmsExactBinding()
    {
        var options = ContextOptions.Create(_databasePath, pooling: false);
        await using (var dbContext = new ProjectMemoryDbContext(options))
        {
            await dbContext.Database.MigrateAsync();
        }

        var projectId = Guid.NewGuid();
        var registry = CreateRegistry(_databasePath);
        await registry.CreateAsync(projectId, "project-a", ProjectStatus.Active);
        Assert.IsTrue(ContextId.TryParse("chat:Portal", out var contextId));

        var created = await registry.BindAsync(contextId!, projectId);
        Assert.IsTrue(created.Changed);
        Assert.IsNull(created.Error);
        Assert.AreEqual(new ContextBinding(contextId!, projectId, Status.Active), created.Value);
        Assert.AreEqual(created.Value, await registry.FindBindingAsync(contextId!));

        var confirmed = await registry.BindAsync(contextId!, projectId);
        Assert.IsFalse(confirmed.Changed);
        Assert.AreEqual(created.Value, confirmed.Value);
    }

    /// <summary>
    /// Verifies that a transactional bind cannot create a project implicitly.
    /// </summary>
    /// <returns>A task that completes after checking the registry operation.</returns>
    [TestMethod]
    public async Task BindAsyncRejectsUnknownProject()
    {
        var options = ContextOptions.Create(_databasePath, pooling: false);
        await using (var dbContext = new ProjectMemoryDbContext(options))
        {
            await dbContext.Database.MigrateAsync();
        }

        Assert.IsTrue(ContextId.TryParse("git:github.com/Org/Repo", out var contextId));
        var registry = CreateRegistry(_databasePath);
        var result = await registry.BindAsync(contextId!, Guid.NewGuid());
        Assert.AreEqual(RegistryError.ProjectNotFound, result.Error);
        Assert.IsNull(await registry.FindBindingAsync(contextId!));
    }

    /// <summary>
    /// Verifies that binding an existing context to another project fails without retargeting its owner.
    /// </summary>
    /// <returns>A task that completes after checking the registry operation.</returns>
    [TestMethod]
    public async Task BindAsyncRejectsRetargeting()
    {
        var options = ContextOptions.Create(_databasePath, pooling: false);
        await using (var dbContext = new ProjectMemoryDbContext(options))
        {
            await dbContext.Database.MigrateAsync();
        }

        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var registry = CreateRegistry(_databasePath);
        await registry.CreateAsync(firstId, "project-a", ProjectStatus.Active);
        await registry.CreateAsync(secondId, "project-b", ProjectStatus.Active);
        Assert.IsTrue(ContextId.TryParse("git:github.com/Org/Repo", out var contextId));

        Assert.IsTrue((await registry.BindAsync(contextId!, firstId)).Changed);
        var rejected = await registry.BindAsync(contextId!, secondId);
        Assert.AreEqual(RegistryError.Conflict, rejected.Error);
        Assert.AreEqual(firstId, (await registry.FindBindingAsync(contextId!))!.MemoryProjectId);
    }

    /// <summary>
    /// Verifies that archiving a project persists the requested lifecycle status.
    /// </summary>
    /// <returns>A task that completes after checking the registry operation.</returns>
    [TestMethod]
    public async Task SetProjectStatusAsyncArchivesProject()
    {
        var projectId = Guid.NewGuid();
        await CreateDatabaseAsync(_databasePath, new ProjectRoutingEntity
        {
            MemoryProjectId = projectId,
            MemoryProjectName = "project-a",
            Status = ProjectStatus.Active
        });

        var registry = CreateRegistry(_databasePath);
        var result = await registry.SetProjectStatusAsync(projectId, ProjectStatus.Archived);
        Assert.IsTrue(result.Changed);
        Assert.AreEqual(ProjectStatus.Archived, result.Value!.Status);
        Assert.AreEqual(ProjectStatus.Archived, (await registry.FindByMemoryProjectIdAsync(projectId))!.Status);
    }

    /// <summary>
    /// Verifies that setting an already-persisted project status is an idempotent no-op.
    /// </summary>
    /// <returns>A task that completes after checking the registry operation.</returns>
    [TestMethod]
    public async Task SetProjectStatusAsyncIsIdempotent()
    {
        var projectId = Guid.NewGuid();
        await CreateDatabaseAsync(_databasePath, new ProjectRoutingEntity
        {
            MemoryProjectId = projectId,
            MemoryProjectName = "project-a",
            Status = ProjectStatus.Archived
        });

        var registry = CreateRegistry(_databasePath);
        var result = await registry.SetProjectStatusAsync(projectId, ProjectStatus.Archived);
        Assert.IsFalse(result.Changed);
        Assert.IsNull(result.Error);
        Assert.AreEqual(ProjectStatus.Archived, (await registry.FindByMemoryProjectIdAsync(projectId))!.Status);
    }

    /// <summary>
    /// Verifies that changing an unknown project status fails without creating registry state.
    /// </summary>
    /// <returns>A task that completes after checking the registry operation.</returns>
    [TestMethod]
    public async Task SetProjectStatusAsyncRejectsUnknownProject()
    {
        var options = ContextOptions.Create(_databasePath, pooling: false);
        await using (var dbContext = new ProjectMemoryDbContext(options))
        {
            await dbContext.Database.MigrateAsync();
        }

        var registry = CreateRegistry(_databasePath);
        var result = await registry.SetProjectStatusAsync(Guid.NewGuid(), ProjectStatus.Archived);
        Assert.AreEqual(RegistryError.ProjectNotFound, result.Error);
    }

    /// <summary>
    /// Verifies that the transactional lifecycle operation rejects unsupported target statuses.
    /// </summary>
    /// <returns>A task that completes after checking the registry operation.</returns>
    [TestMethod]
    public async Task SetProjectStatusAsyncRejectsUnsupportedStatus()
    {
        var registry = CreateRegistry(_databasePath);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => registry.SetProjectStatusAsync(Guid.NewGuid(), (ProjectStatus)int.MaxValue));
    }

    /// <summary>
    /// Verifies that archived projects and inactive persisted bindings fail closed and restored active routes resolve again.
    /// </summary>
    /// <returns>A task that completes after checking the registry operation.</returns>
    [TestMethod]
    public async Task ProjectAndBindingStatusAffectRuntimeContextResolution()
    {
        var projectId = Guid.NewGuid();
        const string context = "git:github.com/Org/Repo";
        await CreateDatabaseAsync(_databasePath, new ProjectRoutingEntity
        {
            MemoryProjectId = projectId,
            MemoryProjectName = "project-a",
            Status = ProjectStatus.Active,
            Bindings = { new ContextBindingEntity { BindingType = BindingType.GitRepository, BindingName = "github.com/Org/Repo", Status = Status.Active } }
        });

        var registry = CreateRegistry(_databasePath);
        var projects = new ProjectManager(registry, new ProjectLocks());
        Assert.AreEqual(projectId, (await projects.ResolveAsync(context)).Data!.ProjectId);

        Assert.IsTrue((await registry.SetProjectStatusAsync(projectId, ProjectStatus.Archived)).Changed);
        Assert.AreEqual("PROJECT_ARCHIVED", (await projects.ResolveAsync(context)).Error!.Code);
        Assert.IsTrue((await registry.SetProjectStatusAsync(projectId, ProjectStatus.Active)).Changed);
        Assert.AreEqual(projectId, (await projects.ResolveAsync(context)).Data!.ProjectId);

        var options = ContextOptions.Create(_databasePath, pooling: false);
        await using (var dbContext = new ProjectMemoryDbContext(options))
        {
            await dbContext.ContextBindings.ExecuteUpdateAsync(update => update.SetProperty(binding => binding.Status, Status.Inactive));
        }

        Assert.AreEqual("CONTEXT_UNBOUND", (await projects.ResolveAsync(context)).Error!.Code);
        Assert.IsTrue(ContextId.TryParse(context, out var contextId));
        Assert.AreEqual(RegistryError.Conflict, (await registry.BindAsync(contextId!, projectId)).Error);
    }

    /// <summary>
    /// Verifies that removing an active exact context binding deletes only the binding and preserves its project routing.
    /// </summary>
    /// <returns>A task that completes when the test has verified that removing an active exact context binding deletes only the binding and preserves its project routing.</returns>
    [TestMethod]
    public async Task TryRemoveBindingAsyncRemovesActiveBindingAndPreservesProjectRouting()
    {
        var memoryProjectId = Guid.NewGuid();
        await CreateDatabaseAsync(_databasePath, new ProjectRoutingEntity
        {
            MemoryProjectId = memoryProjectId,
            MemoryProjectName = "project-a",
            Status = ProjectStatus.Active,
            Bindings =
                {
                    new ContextBindingEntity { BindingType = BindingType.GitRepository, BindingName = "github.com/ChrSchu90/ProjectMemory", Status = Status.Active },
                    new ContextBindingEntity { BindingType = BindingType.ChatProject, BindingName = "chatty-mcp-and-aiharborvm", Status = Status.Active }
                }
        });

        Assert.IsTrue(ContextId.TryParse("git:github.com/ChrSchu90/ProjectMemory", out var contextId));
        Assert.IsTrue(ContextId.TryParse("chat:chatty-mcp-and-aiharborvm", out var otherContextId));
        var registry = CreateRegistry(_databasePath);
        var removed = await registry.TryRemoveBindingAsync(contextId!, CancellationToken.None);
        Assert.IsTrue(removed);
        Assert.IsNull(await registry.FindBindingAsync(contextId!, CancellationToken.None));
        Assert.IsNotNull(await registry.FindBindingAsync(otherContextId!, CancellationToken.None));

        var routing = await registry.FindByMemoryProjectIdAsync(memoryProjectId, CancellationToken.None);
        Assert.IsNotNull(routing);
        Assert.AreEqual(ProjectStatus.Active, routing.Status);
    }

    /// <summary>
    /// Verifies that removing an inactive exact context binding physically deletes it instead of changing its status.
    /// </summary>
    /// <returns>A task that completes when the test has verified that removing an inactive exact context binding physically deletes it instead of changing its status.</returns>
    [TestMethod]
    public async Task TryRemoveBindingAsyncRemovesInactiveBinding()
    {
        await CreateDatabaseAsync(_databasePath, new ProjectRoutingEntity
        {
            MemoryProjectId = Guid.NewGuid(),
            MemoryProjectName = "project-a",
            Status = ProjectStatus.Active,
            Bindings =
                {
                    new ContextBindingEntity
                    {
                        BindingType = BindingType.GitRepository,
                        BindingName = "github.com/ChrSchu90/ProjectMemory",
                        Status = Status.Inactive
                    }
                }
        });

        Assert.IsTrue(ContextId.TryParse("git:github.com/ChrSchu90/ProjectMemory", out var contextId));
        var registry = CreateRegistry(_databasePath);
        var removed = await registry.TryRemoveBindingAsync(contextId!, CancellationToken.None);
        Assert.IsTrue(removed);
        Assert.IsNull(await registry.FindBindingAsync(contextId!, CancellationToken.None));
    }

    /// <summary>
    /// Verifies that removing an unknown exact context binding returns false without creating or changing registry state.
    /// </summary>
    /// <returns>A task that completes when the test has verified that removing an unknown exact context binding returns false without creating or changing registry state.</returns>
    [TestMethod]
    public async Task TryRemoveBindingAsyncReturnsFalseForUnknownContext()
    {
        var options = ContextOptions.Create(_databasePath, pooling: false);
        await using (var dbContext = new ProjectMemoryDbContext(options))
        {
            await dbContext.Database.MigrateAsync();
        }

        Assert.IsTrue(ContextId.TryParse("git:github.com/ChrSchu90/ProjectMemory", out var contextId));
        var registry = CreateRegistry(_databasePath);
        Assert.IsFalse(await registry.TryRemoveBindingAsync(contextId!, CancellationToken.None));
    }

    /// <summary>
    /// Verifies that removing an exact binding blocks resolution until an explicit rebind recreates it.
    /// </summary>
    /// <returns>A task that completes after checking the registry operation.</returns>
    [TestMethod]
    public async Task BindingRemovalAndRebindAffectRuntimeContextResolution()
    {
        var options = ContextOptions.Create(_databasePath, pooling: false);
        await using (var dbContext = new ProjectMemoryDbContext(options))
        {
            await dbContext.Database.MigrateAsync();
        }

        var projectId = Guid.NewGuid();
        const string context = "git:github.com/Org/Repo";
        Assert.IsTrue(ContextId.TryParse(context, out var contextId));
        var registry = CreateRegistry(_databasePath);
        var projects = new ProjectManager(registry, new ProjectLocks());
        await registry.CreateAsync(projectId, "project-a", ProjectStatus.Active);
        Assert.IsTrue((await registry.BindAsync(contextId!, projectId)).Changed);
        Assert.AreEqual(projectId, (await projects.ResolveAsync(context)).Data!.ProjectId);

        Assert.IsTrue(await registry.TryRemoveBindingAsync(contextId!));
        Assert.AreEqual("CONTEXT_UNBOUND", (await projects.ResolveAsync(context)).Error!.Code);

        Assert.IsTrue((await registry.BindAsync(contextId!, projectId)).Changed);
        var rebound = await projects.ResolveAsync(context);
        Assert.AreEqual(projectId, rebound.Data!.ProjectId);
        Assert.AreEqual("project-a", rebound.Data.ProjectName);
    }

    /// <summary>
    /// Verifies that case variants register independently and wrong-case lookups never select another project.
    /// </summary>
    /// <param name="upper">The context ID containing uppercase characters to register for the first project.</param>
    /// <param name="lower">The distinct case variant to register independently for the second project.</param>
    /// <returns>A task that completes when the test has verified that case variants register independently and wrong-case lookups never select another project.</returns>
    [TestMethod]
    [DataRow("git:github.com/Org/Repo", "git:github.com/org/repo")]
    [DataRow("chat:Portal", "chat:portal")]
    [DataRow("local:Workspace", "local:workspace")]
    public async Task ExactCaseBindingsRemainIndependent(string upper, string lower)
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var a = await fixture.CreateProjectAsync("a");
        var b = await fixture.CreateProjectAsync("b");
        var first = await fixture.Registry.BindAsync(DatabaseFixture.ParseContext(upper), a.MemoryProjectId);
        var second = await fixture.Registry.BindAsync(DatabaseFixture.ParseContext(lower), b.MemoryProjectId);
        Assert.IsTrue(first.Changed);
        Assert.IsTrue(second.Changed);
        var projects = fixture.Services.GetRequiredService<ProjectManager>();
        Assert.AreEqual(a.MemoryProjectId, (await projects.ResolveAsync(upper)).Data!.ProjectId);
        Assert.AreEqual(b.MemoryProjectId, (await projects.ResolveAsync(lower)).Data!.ProjectId);
        Assert.AreEqual("CONTEXT_UNBOUND", (await projects.ResolveAsync(upper + "X")).Error!.Code);
        Assert.HasCount(2, await fixture.Registry.ListProjectsPageAsync(0, 10));
        Assert.HasCount(2, await fixture.Registry.ListBindingsPageAsync(null, 0, 10));
    }

    /// <summary>
    /// Verifies that concurrent bind calls either confirm one owner or return conflict without retargeting it.
    /// </summary>
    /// <param name="sameProject">Whether both concurrent bind attempts target the same project rather than different projects.</param>
    /// <returns>A task that completes when the test has verified that concurrent bind calls either confirm one owner or return conflict without retargeting it.</returns>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ConcurrentBindingsNeverRetarget(bool sameProject)
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var a = await fixture.CreateProjectAsync("a");
        var b = sameProject ? a : await fixture.CreateProjectAsync("b");
        var context = DatabaseFixture.ParseContext("chat:Concurrent");
        using var gate = new ManualResetEventSlim();
        var first = Task.Run(async () => { gate.Wait(); return await fixture.Registry.BindAsync(context, a.MemoryProjectId); });

        var second = Task.Run(async () => { gate.Wait(); return await fixture.Registry.BindAsync(context, b.MemoryProjectId); });

        gate.Set();
        var results = await Task.WhenAll(first, second);
        Assert.AreEqual(1, results.Count(result => result.Changed));
        Assert.AreEqual(sameProject ? 0 : 1, results.Count(result => result.Error == RegistryError.Conflict));
        var winner = results.Single(result => result.Changed).Value!;
        var stored = await fixture.Registry.FindBindingAsync(context);
        Assert.AreEqual(winner.MemoryProjectId, stored!.MemoryProjectId);
        Assert.HasCount(1, await fixture.Registry.ListBindingsPageAsync(null, 0, 10));
    }

    /// <summary>
    /// Verifies that binding and archival races have a serial outcome, block resolution and preserve unrelated projects.
    /// </summary>
    /// <returns>A task that completes when the test has verified that binding and archival races have a serial outcome, block resolution and preserve unrelated projects.</returns>
    [TestMethod]
    public async Task ArchiveAndBindAreSerialized()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var a = await fixture.CreateProjectAsync("a");
        var b = await fixture.CreateProjectAsync("b");
        await fixture.Registry.BindAsync(DatabaseFixture.ParseContext("chat:Other"), b.MemoryProjectId);
        using var gate = new ManualResetEventSlim();
        var bind = Task.Run(async () => { gate.Wait(); return await fixture.Registry.BindAsync(DatabaseFixture.ParseContext("chat:Race"), a.MemoryProjectId); });

        var archive = Task.Run(async () => { gate.Wait(); return await fixture.Registry.SetProjectStatusAsync(a.MemoryProjectId, ProjectStatus.Archived); });

        gate.Set();
        await Task.WhenAll(bind, archive);
        var result = await bind;
        Assert.IsTrue(result.Error is null or RegistryError.ProjectArchived);
        Assert.AreEqual(ProjectStatus.Archived, (await fixture.Registry.FindByMemoryProjectIdAsync(a.MemoryProjectId))!.Status);
        var projects = fixture.Services.GetRequiredService<ProjectManager>();
        var archivedResolution = await projects.ResolveAsync("chat:Race");
        Assert.IsFalse(archivedResolution.Ok);
        Assert.AreEqual(result.Error is null ? "PROJECT_ARCHIVED" : "CONTEXT_UNBOUND", archivedResolution.Error!.Code);
        Assert.AreEqual(b.MemoryProjectId, (await projects.ResolveAsync("chat:Other")).Data!.ProjectId);
        await fixture.Registry.SetProjectStatusAsync(a.MemoryProjectId, ProjectStatus.Active);
        var resolution = await projects.ResolveAsync("chat:Race");
        Assert.AreEqual(result.Error is null, resolution.Ok);
        if (resolution.Ok)
            Assert.AreEqual(a.MemoryProjectId, resolution.Data!.ProjectId);
        else
            Assert.AreEqual("CONTEXT_UNBOUND", resolution.Error!.Code);
    }

    /// <summary>
    /// Verifies that rejected or cancelled writes leave project IDs, lifecycle and bindings unchanged.
    /// </summary>
    /// <returns>A task that completes when the test has verified that rejected or cancelled writes leave project IDs, lifecycle and bindings unchanged.</returns>
    [TestMethod]
    public async Task FailedAndCancelledWritesDoNotMutateState()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var a = await fixture.CreateProjectAsync("a");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Registry.BindAsync(DatabaseFixture.ParseContext("chat:Cancelled"), a.MemoryProjectId, cancelled.Token));
        Assert.AreEqual(RegistryError.ProjectNotFound, (await fixture.Registry.BindAsync(DatabaseFixture.ParseContext("chat:Missing"), Guid.NewGuid())).Error);
        await using (var context = fixture.CreateContext())
        {
            await context.Database.ExecuteSqlRawAsync("CREATE TRIGGER RejectProjectUpdate BEFORE UPDATE ON ProjectRoutings BEGIN SELECT RAISE(ABORT, 'isolated fault'); END;");
        }

        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Registry.SetProjectStatusAsync(a.MemoryProjectId, ProjectStatus.Archived));
        Assert.AreEqual(a, await fixture.Registry.FindByMemoryProjectIdAsync(a.MemoryProjectId));
        Assert.IsEmpty(await fixture.Registry.ListBindingsPageAsync(null, 0, 10));
    }

    #endregion

    #region Private Methods

    private static EfProjectRegistry CreateRegistry(string databasePath)
    {
        var options = ContextOptions.Create(databasePath, pooling: false);
        return new EfProjectRegistry(new TestDbContextFactory(options));
    }

    private static async Task CreateDatabaseAsync(string databasePath, params ProjectRoutingEntity[] projectRoutings)
    {
        var options = ContextOptions.Create(databasePath, pooling: false);

        await using var dbContext = new ProjectMemoryDbContext(options);
        await dbContext.Database.MigrateAsync();

        foreach (var projectRouting in projectRoutings)
        {
            dbContext.RoutingProjects.Add(projectRouting);
        }

        await dbContext.SaveChangesAsync();
    }

    private static void DeleteDatabase(string databasePath)
    {
        foreach (var suffix in new[] { "", "-journal", "-shm", "-wal" })
        {
            var path = databasePath + suffix;

            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    #endregion

    #region Test Classes

    private sealed class TestDbContextFactory : IDbContextFactory<ProjectMemoryDbContext>
    {
        #region Static Fields

        #endregion

        #region Private Fields

        private readonly DbContextOptions<ProjectMemoryDbContext> _options;

        #endregion

        #region Constructors

        /// <summary>
        /// Initializes a factory that creates contexts for the test's isolated database.
        /// </summary>
        /// <param name="options">The context options selecting the disposable test database.</param>
        public TestDbContextFactory(DbContextOptions<ProjectMemoryDbContext> options)
        {
            _options = options;
        }

        #endregion

        #region Properties

        #endregion

        #region Public Methods

        /// <inheritdoc />
        public ProjectMemoryDbContext CreateDbContext()
        {
            return new ProjectMemoryDbContext(_options);
        }

        /// <inheritdoc />
        public Task<ProjectMemoryDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ProjectMemoryDbContext(_options));
        }

        #endregion

        #region Private Methods

        #endregion
    }

    #endregion
}
