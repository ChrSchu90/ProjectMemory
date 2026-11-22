namespace ProjectMemory.Tests.Database.Routing;

using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjectMemory.Database.Entities;
using ProjectMemory.Database.Routing;
using ProjectMemory.Database;
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
    /// Verifies that a registered project routing can be found by its exact project name.
    /// </summary>
    /// <returns>A task that completes when the test has verified that a registered project routing can be found by its exact project name.</returns>
    [TestMethod]
    public async Task FindByMemoryProjectNameAsyncReturnsRouting()
    {
        var memoryProjectId = Guid.NewGuid();
        var memoryProjectName = "project-a";
        await CreateDatabaseAsync(_databasePath, new ProjectRoutingEntity
        {
            MemoryProjectId = memoryProjectId,
            MemoryProjectName = memoryProjectName,
            Status = ProjectStatus.Active
        });

        var registry = CreateRegistry(_databasePath);
        var routing = await registry.FindByMemoryProjectNameAsync(memoryProjectName, CancellationToken.None);
        Assert.IsNotNull(routing);
        Assert.AreEqual(memoryProjectId, routing.MemoryProjectId);
        Assert.AreEqual(memoryProjectName, routing.MemoryProjectName);
        Assert.AreEqual(ProjectStatus.Active, routing.Status);
    }

    /// <summary>
    /// Verifies that project-routing lookups return no result when the requested identity is not registered.
    /// </summary>
    /// <returns>A task that completes when the test has verified that project-routing lookups return no result when the requested identity is not registered.</returns>
    [TestMethod]
    public async Task ProjectRoutingLookupsReturnNullWhenRoutingDoesNotExist()
    {
        await CreateDatabaseAsync(_databasePath, new ProjectRoutingEntity
        {
            MemoryProjectId = Guid.NewGuid(),
            MemoryProjectName = "project-a"
        });

        var registry = CreateRegistry(_databasePath);
        var byId = await registry.FindByMemoryProjectIdAsync(Guid.NewGuid(), CancellationToken.None);
        var byName = await registry.FindByMemoryProjectNameAsync("missing-project", CancellationToken.None);
        Assert.IsNull(byId);
        Assert.IsNull(byName);
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
    /// Verifies that all registered context bindings are listed with exact context identifiers, targets, statuses, and deterministic ordering.
    /// </summary>
    /// <returns>A task that completes when the test has verified that all registered context bindings are listed with exact context identifiers, targets, statuses, and deterministic ordering.</returns>
    [TestMethod]
    public async Task ListBindingsAsyncReturnsAllBindings()
    {
        var chatGptProjectId = Guid.NewGuid();
        var gitProjectId = Guid.NewGuid();
        await CreateDatabaseAsync(_databasePath,
            new ProjectRoutingEntity
            {
                MemoryProjectId = chatGptProjectId,
                MemoryProjectName = "chatgpt-project",
                Bindings =
                        {
                            new ContextBindingEntity
                                {
                                    BindingType = BindingType.ChatProject,
                                    BindingName = "chatty-mcp-and-aiharborvm",
                                    Status = Status.Inactive
                                }
                        }
            },
            new ProjectRoutingEntity
            {
                MemoryProjectId = gitProjectId,
                MemoryProjectName = "git-project",
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
        var bindings = await registry.ListBindingsAsync(CancellationToken.None);

        Assert.HasCount(2, bindings);
        Assert.AreEqual("chat:chatty-mcp-and-aiharborvm", bindings[0].ContextId.ToString());
        Assert.AreEqual(chatGptProjectId, bindings[0].MemoryProjectId);
        Assert.AreEqual(Status.Inactive, bindings[0].Status);
        Assert.AreEqual("git:github.com/ChrSchu90/ProjectMemory", bindings[1].ContextId.ToString());
        Assert.AreEqual(gitProjectId, bindings[1].MemoryProjectId);
        Assert.AreEqual(Status.Active, bindings[1].Status);
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
    /// Verifies that creating a context binding persists the requested target project and initial status.
    /// </summary>
    /// <returns>A task that completes when the test has verified that creating a context binding persists the requested target project and initial status.</returns>
    [TestMethod]
    public async Task CreateBindingAsyncPersistsBinding()
    {
        var options = ContextOptions.Create(_databasePath, pooling: false);
        await using (var dbContext = new ProjectMemoryDbContext(options))
        {
            await dbContext.Database.MigrateAsync();
        }

        var memoryProjectId = Guid.NewGuid();
        var registry = CreateRegistry(_databasePath);

        await registry.CreateAsync(memoryProjectId, "project-a", ProjectStatus.Active, CancellationToken.None);
        Assert.IsTrue(ContextId.TryParse("chat:chatty-mcp-and-aiharborvm", out var contextId));

        var created = await registry.CreateBindingAsync(contextId!, memoryProjectId, Status.Inactive, CancellationToken.None);
        Assert.AreEqual(contextId, created.ContextId);
        Assert.AreEqual(memoryProjectId, created.MemoryProjectId);
        Assert.AreEqual(Status.Inactive, created.Status);

        var persisted = await registry.FindBindingAsync(contextId!, CancellationToken.None);
        Assert.IsNotNull(persisted);
        Assert.AreEqual(created, persisted);
    }

    /// <summary>
    /// Verifies that creating a context binding fails when its target project routing is not registered.
    /// </summary>
    /// <returns>A task that completes when the test has verified that creating a context binding fails when its target project routing is not registered.</returns>
    [TestMethod]
    public async Task CreateBindingAsyncRejectsMissingProjectRouting()
    {
        var options = ContextOptions.Create(_databasePath, pooling: false);
        await using (var dbContext = new ProjectMemoryDbContext(options))
        {
            await dbContext.Database.MigrateAsync();
        }

        var registry = CreateRegistry(_databasePath);
        Assert.IsTrue(ContextId.TryParse("git:github.com/ChrSchu90/ProjectMemory", out var contextId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.CreateBindingAsync(contextId!, Guid.NewGuid(), Status.Active, CancellationToken.None));
    }

    /// <summary>
    /// Verifies that creating a duplicate exact context binding reports a registry conflict instead of leaking an EF Core exception.
    /// </summary>
    /// <returns>A task that completes when the test has verified that creating a duplicate exact context binding reports a registry conflict instead of leaking an EF Core exception.</returns>
    [TestMethod]
    public async Task CreateBindingAsyncReportsDuplicateContextConflict()
    {
        var options = ContextOptions.Create(_databasePath, pooling: false);
        await using (var dbContext = new ProjectMemoryDbContext(options))
        {
            await dbContext.Database.MigrateAsync();
        }

        var projectAId = Guid.NewGuid();
        var projectBId = Guid.NewGuid();
        var registry = CreateRegistry(_databasePath);

        await registry.CreateAsync(projectAId, "project-a", ProjectStatus.Active, CancellationToken.None);
        await registry.CreateAsync(projectBId, "project-b", ProjectStatus.Active, CancellationToken.None);
        Assert.IsTrue(ContextId.TryParse("git:github.com/ChrSchu90/ProjectMemory", out var contextId));

        await registry.CreateBindingAsync(contextId!, projectAId, Status.Active, CancellationToken.None);
        await Assert.ThrowsAsync<ProjectRegistryConflictException>(() => registry.CreateBindingAsync(contextId!, projectBId, Status.Active, CancellationToken.None));
    }

    /// <summary>
    /// Verifies that creating a context binding rejects unsupported status values before modifying persistence.
    /// </summary>
    /// <returns>A task that completes when the test has verified that creating a context binding rejects unsupported status values before modifying persistence.</returns>
    [TestMethod]
    public async Task CreateBindingAsyncRejectsUnsupportedStatus()
    {
        var options = ContextOptions.Create(_databasePath, pooling: false);
        await using (var dbContext = new ProjectMemoryDbContext(options))
        {
            await dbContext.Database.MigrateAsync();
        }

        var memoryProjectId = Guid.NewGuid();
        var registry = CreateRegistry(_databasePath);

        await registry.CreateAsync(memoryProjectId, "project-a", ProjectStatus.Active, CancellationToken.None);
        Assert.IsTrue(ContextId.TryParse("git:github.com/ChrSchu90/ProjectMemory", out var contextId));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => registry.CreateBindingAsync(contextId!, memoryProjectId, (Status)int.MaxValue, CancellationToken.None));
    }

    /// <summary>
    /// Verifies that a project-routing status is updated when the persisted status matches the expected status.
    /// </summary>
    /// <returns>A task that completes when the test has verified that a project-routing status is updated when the persisted status matches the expected status.</returns>
    [TestMethod]
    public async Task TryUpdateProjectStatusAsyncUpdatesMatchingStatus()
    {
        var memoryProjectId = Guid.NewGuid();
        await CreateDatabaseAsync(_databasePath, new ProjectRoutingEntity
        {
            MemoryProjectId = memoryProjectId,
            MemoryProjectName = "project-a",
            Status = ProjectStatus.Active
        });

        var registry = CreateRegistry(_databasePath);
        var updated = await registry.TryUpdateProjectStatusAsync(memoryProjectId, ProjectStatus.Active, ProjectStatus.Archived, CancellationToken.None);
        Assert.IsTrue(updated);

        var persisted = await registry.FindByMemoryProjectIdAsync(memoryProjectId, CancellationToken.None);
        Assert.IsNotNull(persisted);
        Assert.AreEqual(ProjectStatus.Archived, persisted.Status);
    }

    /// <summary>
    /// Verifies that a project-routing status is not changed when the persisted status no longer matches the expected status.
    /// </summary>
    /// <returns>A task that completes when the test has verified that a project-routing status is not changed when the persisted status no longer matches the expected status.</returns>
    [TestMethod]
    public async Task TryUpdateProjectStatusAsyncRejectsStaleExpectedStatus()
    {
        var memoryProjectId = Guid.NewGuid();
        await CreateDatabaseAsync(_databasePath, new ProjectRoutingEntity
        {
            MemoryProjectId = memoryProjectId,
            MemoryProjectName = "project-a",
            Status = ProjectStatus.Archived
        });

        var registry = CreateRegistry(_databasePath);
        var updated = await registry.TryUpdateProjectStatusAsync(memoryProjectId, ProjectStatus.Active, ProjectStatus.Archived, CancellationToken.None);
        Assert.IsFalse(updated);

        var persisted = await registry.FindByMemoryProjectIdAsync(memoryProjectId, CancellationToken.None);
        Assert.IsNotNull(persisted);
        Assert.AreEqual(ProjectStatus.Archived, persisted.Status);
    }

    /// <summary>
    /// Verifies that a context-binding status is updated when the persisted status matches the expected status.
    /// </summary>
    /// <returns>A task that completes when the test has verified that a context-binding status is updated when the persisted status matches the expected status.</returns>
    [TestMethod]
    public async Task TryUpdateBindingStatusAsyncUpdatesMatchingStatus()
    {
        var memoryProjectId = Guid.NewGuid();
        await CreateDatabaseAsync(_databasePath, new ProjectRoutingEntity
        {
            MemoryProjectId = memoryProjectId,
            MemoryProjectName = "project-a",
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

        Assert.IsTrue(ContextId.TryParse("git:github.com/ChrSchu90/ProjectMemory", out var contextId));
        var registry = CreateRegistry(_databasePath);
        var updated = await registry.TryUpdateBindingStatusAsync(contextId!, Status.Active, Status.Inactive, CancellationToken.None);
        Assert.IsTrue(updated);

        var persisted = await registry.FindBindingAsync(contextId!, CancellationToken.None);
        Assert.IsNotNull(persisted);
        Assert.AreEqual(Status.Inactive, persisted.Status);
    }

    /// <summary>
    /// Verifies that a context-binding status is not changed when the persisted status no longer matches the expected status.
    /// </summary>
    /// <returns>A task that completes when the test has verified that a context-binding status is not changed when the persisted status no longer matches the expected status.</returns>
    [TestMethod]
    public async Task TryUpdateBindingStatusAsyncRejectsStaleExpectedStatus()
    {
        var memoryProjectId = Guid.NewGuid();
        await CreateDatabaseAsync(_databasePath, new ProjectRoutingEntity
        {
            MemoryProjectId = memoryProjectId,
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
        var updated = await registry.TryUpdateBindingStatusAsync(contextId!, Status.Active, Status.Inactive, CancellationToken.None);
        Assert.IsFalse(updated);

        var persisted = await registry.FindBindingAsync(contextId!, CancellationToken.None);
        Assert.IsNotNull(persisted);
        Assert.AreEqual(Status.Inactive, persisted.Status);
    }

    /// <summary>
    /// Verifies that changing the status of an unknown project routing returns false without creating registry state.
    /// </summary>
    /// <returns>A task that completes when the test has verified that changing the status of an unknown project routing returns false without creating registry state.</returns>
    [TestMethod]
    public async Task TryUpdateProjectStatusAsyncReturnsFalseForUnknownProject()
    {
        var options = ContextOptions.Create(_databasePath, pooling: false);
        await using (var dbContext = new ProjectMemoryDbContext(options))
        {
            await dbContext.Database.MigrateAsync();
        }

        var registry = CreateRegistry(_databasePath);
        var updated = await registry.TryUpdateProjectStatusAsync(Guid.NewGuid(), ProjectStatus.Active, ProjectStatus.Archived, CancellationToken.None);
        Assert.IsFalse(updated);
    }

    /// <summary>
    /// Verifies that changing the status of an unknown context binding returns false without creating registry state.
    /// </summary>
    /// <returns>A task that completes when the test has verified that changing the status of an unknown context binding returns false without creating registry state.</returns>
    [TestMethod]
    public async Task TryUpdateBindingStatusAsyncReturnsFalseForUnknownContext()
    {
        var options = ContextOptions.Create(_databasePath, pooling: false);
        await using (var dbContext = new ProjectMemoryDbContext(options))
        {
            await dbContext.Database.MigrateAsync();
        }

        Assert.IsTrue(ContextId.TryParse("git:github.com/ChrSchu90/ProjectMemory", out var contextId));
        var registry = CreateRegistry(_databasePath);
        var updated = await registry.TryUpdateBindingStatusAsync(contextId!, Status.Active, Status.Inactive, CancellationToken.None);
        Assert.IsFalse(updated);
    }

    /// <summary>
    /// Verifies that project-routing status updates reject unsupported expected and target status values.
    /// </summary>
    /// <returns>A task that completes when the test has verified that project-routing status updates reject unsupported expected and target status values.</returns>
    [TestMethod]
    public async Task TryUpdateProjectStatusAsyncRejectsUnsupportedStatuses()
    {
        var registry = CreateRegistry(_databasePath);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => registry.TryUpdateProjectStatusAsync(Guid.NewGuid(), (ProjectStatus)int.MaxValue, ProjectStatus.Active, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => registry.TryUpdateProjectStatusAsync(Guid.NewGuid(), ProjectStatus.Active, (ProjectStatus)int.MaxValue, CancellationToken.None));
    }

    /// <summary>
    /// Verifies that context-binding status updates reject unsupported expected and target status values.
    /// </summary>
    /// <returns>A task that completes when the test has verified that context-binding status updates reject unsupported expected and target status values.</returns>
    [TestMethod]
    public async Task TryUpdateBindingStatusAsyncRejectsUnsupportedStatuses()
    {
        Assert.IsTrue(ContextId.TryParse("git:github.com/ChrSchu90/ProjectMemory", out var contextId));
        var registry = CreateRegistry(_databasePath);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => registry.TryUpdateBindingStatusAsync(contextId!, (Status)int.MaxValue, Status.Active, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => registry.TryUpdateBindingStatusAsync(contextId!, Status.Active, (Status)int.MaxValue, CancellationToken.None));
    }

    /// <summary>
    /// Verifies that project-routing and context-binding status transitions immediately affect runtime context resolution.
    /// </summary>
    /// <returns>A task that completes when the test has verified that project-routing and context-binding status transitions immediately affect runtime context resolution.</returns>
    [TestMethod]
    public async Task StatusTransitionsAffectRuntimeContextResolution()
    {
        var options = ContextOptions.Create(_databasePath, pooling: false);
        await using (var dbContext = new ProjectMemoryDbContext(options))
        {
            await dbContext.Database.MigrateAsync();
        }

        var memoryProjectId = Guid.NewGuid();

        const string memoryProjectName = "project-a";
        Assert.IsTrue(ContextId.TryParse("git:github.com/ChrSchu90/ProjectMemory", out var contextId));

        var registry = CreateRegistry(_databasePath);
        var routingManager = new RoutingManager(registry, NullLogger<RoutingManager>.Instance);
        await registry.CreateAsync(memoryProjectId, memoryProjectName, ProjectStatus.Active, CancellationToken.None);
        await registry.CreateBindingAsync(contextId!, memoryProjectId, Status.Active, CancellationToken.None);

        var resolved = await routingManager.ResolveContextAsync(contextId!, CancellationToken.None);
        Assert.AreEqual(ContextResolutionStatus.Resolved, resolved.Status);
        Assert.AreEqual(memoryProjectId, resolved.MemoryProjectId);
        Assert.AreEqual(memoryProjectName, resolved.MemoryProjectName);
        Assert.IsTrue(await registry.TryUpdateBindingStatusAsync(contextId!, Status.Active, Status.Inactive, CancellationToken.None));

        var bindingInactive = await routingManager.ResolveContextAsync(contextId!, CancellationToken.None);
        Assert.AreEqual(ContextResolutionStatus.BindingInactive, bindingInactive.Status);
        Assert.IsTrue(await registry.TryUpdateBindingStatusAsync(contextId!, Status.Inactive, Status.Active, CancellationToken.None));

        var bindingReactivated = await routingManager.ResolveContextAsync(contextId!, CancellationToken.None);
        Assert.AreEqual(ContextResolutionStatus.Resolved, bindingReactivated.Status);
        Assert.IsTrue(await registry.TryUpdateProjectStatusAsync(memoryProjectId, ProjectStatus.Active, ProjectStatus.Archived, CancellationToken.None));

        var projectInactive = await routingManager.ResolveContextAsync(contextId!, CancellationToken.None);
        Assert.AreEqual(ContextResolutionStatus.ProjectRoutingInactive, projectInactive.Status);
        Assert.IsTrue(await registry.TryUpdateProjectStatusAsync(memoryProjectId, ProjectStatus.Archived, ProjectStatus.Active, CancellationToken.None));

        var projectReactivated = await routingManager.ResolveContextAsync(contextId!, CancellationToken.None);
        Assert.AreEqual(ContextResolutionStatus.Resolved, projectReactivated.Status);
        Assert.AreEqual(memoryProjectId, projectReactivated.MemoryProjectId);
        Assert.AreEqual(memoryProjectName, projectReactivated.MemoryProjectName);
    }

    /// <summary>
    /// Verifies that removing a project routing physically deletes the routing and all dependent context bindings.
    /// </summary>
    /// <returns>A task that completes when the test has verified that removing a project routing physically deletes the routing and all dependent context bindings.</returns>
    [TestMethod]
    public async Task TryRemoveProjectAsyncRemovesRoutingAndDependentBindings()
    {
        var memoryProjectId = Guid.NewGuid();
        await CreateDatabaseAsync(_databasePath, new ProjectRoutingEntity
        {
            MemoryProjectId = memoryProjectId,
            MemoryProjectName = "project-a",
            Status = ProjectStatus.Active,
            Bindings =
                {
                    new ContextBindingEntity
                        {
                            BindingType = BindingType.GitRepository,
                            BindingName = "github.com/ChrSchu90/ProjectMemory",
                            Status = Status.Active
                        },
                    new ContextBindingEntity
                        {
                            BindingType = BindingType.ChatProject,
                            BindingName = "chatty-mcp-and-aiharborvm",
                            Status = Status.Inactive
                        }
                }
        });

        Assert.IsTrue(ContextId.TryParse("git:github.com/ChrSchu90/ProjectMemory", out var gitContext));
        Assert.IsTrue(ContextId.TryParse("chat:chatty-mcp-and-aiharborvm", out var chatGptContext));

        var registry = CreateRegistry(_databasePath);
        var removed = await registry.TryRemoveProjectAsync(memoryProjectId, CancellationToken.None);
        Assert.IsTrue(removed);
        Assert.IsNull(await registry.FindByMemoryProjectIdAsync(memoryProjectId, CancellationToken.None));
        Assert.IsNull(await registry.FindBindingAsync(gitContext!, CancellationToken.None));
        Assert.IsNull(await registry.FindBindingAsync(chatGptContext!, CancellationToken.None));
    }

    /// <summary>
    /// Verifies that removing an unknown project routing returns false without changing registry state.
    /// </summary>
    /// <returns>A task that completes when the test has verified that removing an unknown project routing returns false without changing registry state.</returns>
    [TestMethod]
    public async Task TryRemoveProjectAsyncReturnsFalseForUnknownProject()
    {
        var options = ContextOptions.Create(_databasePath, pooling: false);
        await using (var dbContext = new ProjectMemoryDbContext(options))
        {
            await dbContext.Database.MigrateAsync();
        }

        var registry = CreateRegistry(_databasePath);
        Assert.IsFalse(await registry.TryRemoveProjectAsync(Guid.NewGuid(), CancellationToken.None));
    }

    /// <summary>
    /// Verifies that removing a project routing rejects an empty project identifier before accessing persistence.
    /// </summary>
    /// <returns>A task that completes when the test has verified that removing a project routing rejects an empty project identifier before accessing persistence.</returns>
    [TestMethod]
    public async Task TryRemoveProjectAsyncRejectsEmptyProjectId()
    {
        var registry = CreateRegistry(_databasePath);
        await Assert.ThrowsAsync<ArgumentException>(() => registry.TryRemoveProjectAsync(Guid.Empty, CancellationToken.None));
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
    /// Verifies that physical binding removal changes runtime resolution to <c>NotBound</c> and a later new binding resolves again.
    /// </summary>
    /// <returns>A task that completes when the test has verified that physical binding removal changes runtime resolution to <c>NotBound</c> and a later new binding resolves again.</returns>
    [TestMethod]
    public async Task BindingRemovalAndRebindAffectRuntimeContextResolution()
    {
        var options = ContextOptions.Create(_databasePath, pooling: false);
        await using (var dbContext = new ProjectMemoryDbContext(options))
        {
            await dbContext.Database.MigrateAsync();
        }

        var memoryProjectId = Guid.NewGuid();
        const string memoryProjectName = "project-a";
        Assert.IsTrue(ContextId.TryParse("git:github.com/ChrSchu90/ProjectMemory", out var contextId));

        var registry = CreateRegistry(_databasePath);
        var routingManager = new RoutingManager(registry, NullLogger<RoutingManager>.Instance);
        await registry.CreateAsync(memoryProjectId, memoryProjectName, ProjectStatus.Active, CancellationToken.None);
        await registry.CreateBindingAsync(contextId!, memoryProjectId, Status.Active, CancellationToken.None);

        var resolved = await routingManager.ResolveContextAsync(contextId!, CancellationToken.None);
        Assert.AreEqual(ContextResolutionStatus.Resolved, resolved.Status);

        Assert.IsTrue(await registry.TryRemoveBindingAsync(contextId!, CancellationToken.None));
        var notBound = await routingManager.ResolveContextAsync(contextId!, CancellationToken.None);
        Assert.AreEqual(ContextResolutionStatus.NotBound, notBound.Status);

        await registry.CreateBindingAsync(contextId!, memoryProjectId, Status.Active, CancellationToken.None);
        var rebound = await routingManager.ResolveContextAsync(contextId!, CancellationToken.None);
        Assert.AreEqual(ContextResolutionStatus.Resolved, rebound.Status);
        Assert.AreEqual(memoryProjectId, rebound.MemoryProjectId);
        Assert.AreEqual(memoryProjectName, rebound.MemoryProjectName);
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
        var routing = fixture.Services.GetRequiredService<RoutingManager>();
        Assert.AreEqual(a.MemoryProjectId, (await routing.ResolveContextAsync(DatabaseFixture.ParseContext(upper))).MemoryProjectId);
        Assert.AreEqual(b.MemoryProjectId, (await routing.ResolveContextAsync(DatabaseFixture.ParseContext(lower))).MemoryProjectId);
        Assert.AreEqual(ContextResolutionStatus.NotBound, (await routing.ResolveContextAsync(DatabaseFixture.ParseContext(upper + "X"))).Status);
        Assert.HasCount(2, await fixture.Registry.ListProjectsAsync());
        Assert.HasCount(2, await fixture.Registry.ListBindingsAsync());
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
        Assert.HasCount(1, await fixture.Registry.ListBindingsAsync());
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
        var routing = fixture.Services.GetRequiredService<RoutingManager>();
        Assert.AreNotEqual(ContextResolutionStatus.Resolved, (await routing.ResolveContextAsync(DatabaseFixture.ParseContext("chat:Race"))).Status);
        Assert.AreEqual(b.MemoryProjectId, (await routing.ResolveContextAsync(DatabaseFixture.ParseContext("chat:Other"))).MemoryProjectId);
        await fixture.Registry.SetProjectStatusAsync(a.MemoryProjectId, ProjectStatus.Active);
        Assert.AreEqual(result.Error is null ? ContextResolutionStatus.Resolved : ContextResolutionStatus.NotBound, (await routing.ResolveContextAsync(DatabaseFixture.ParseContext("chat:Race"))).Status);
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
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Registry.BindAsync(DatabaseFixture.ParseContext("chat:Cancelled"), a.MemoryProjectId, cancelled.Token));
        Assert.AreEqual(RegistryError.ProjectNotFound, (await fixture.Registry.BindAsync(DatabaseFixture.ParseContext("chat:Missing"), Guid.NewGuid())).Error);
        await using (var context = fixture.CreateContext())
        {
            await context.Database.ExecuteSqlRawAsync("CREATE TRIGGER RejectProjectUpdate BEFORE UPDATE ON ProjectRoutings BEGIN SELECT RAISE(ABORT, 'isolated fault'); END;");
        }

        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Registry.SetProjectStatusAsync(a.MemoryProjectId, ProjectStatus.Archived));
        Assert.AreEqual(a, await fixture.Registry.FindByMemoryProjectIdAsync(a.MemoryProjectId));
        Assert.IsEmpty(await fixture.Registry.ListBindingsAsync());
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
