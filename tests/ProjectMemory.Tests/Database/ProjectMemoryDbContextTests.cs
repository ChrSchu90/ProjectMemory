namespace ProjectMemory.Tests.Database;

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjectMemory.Database;
using ProjectMemory.Routing;

/// <summary>
/// Tests the <see cref="ProjectMemoryDbContext"/> model, migration compatibility, persisted enum values and constraints.
/// </summary>
[TestClass]
public sealed class ProjectMemoryDbContextTests
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

    /// <summary>
    /// Verifies that the current EF Core model matches the latest migration and has no pending model changes.
    /// </summary>
    /// <returns>A task that completes when the context model is verified against the latest migration.</returns>
    [TestMethod]
    public async Task NoPendingChanges()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(migrate: false);
        await using var context = fixture.CreateContext();
        Assert.IsFalse(context.Database.HasPendingModelChanges());
    }

    /// <summary>
    /// Verifies that the current schema persists project identities, framework enum names and exact bindings.
    /// </summary>
    /// <returns>A task that completes when the current schema, stored statuses and unchanged registry identities are verified.</returns>
    [TestMethod]
    public async Task CurrentSchemaPersistsProjectsAndBindings()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var a = await fixture.CreateProjectAsync("a");
        var b = await fixture.CreateProjectAsync("b");
        await fixture.Registry.BindAsync(DatabaseFixture.ParseContext("chat:Portal"), a.MemoryProjectId);
        await fixture.Registry.BindAsync(DatabaseFixture.ParseContext("chat:portal"), b.MemoryProjectId);
        await fixture.Registry.SetProjectStatusAsync(b.MemoryProjectId, ProjectStatus.Archived);
        await fixture.Services.MigrateDatabaseAsync();

        Assert.AreEqual(ProjectStatus.Active, (await fixture.Registry.FindByMemoryProjectIdAsync(a.MemoryProjectId))!.Status);
        Assert.AreEqual(ProjectStatus.Archived, (await fixture.Registry.FindByMemoryProjectIdAsync(b.MemoryProjectId))!.Status);
        Assert.AreEqual(a.MemoryProjectId, (await fixture.Registry.FindBindingAsync(DatabaseFixture.ParseContext("chat:Portal")))!.MemoryProjectId);
        Assert.AreEqual(b.MemoryProjectId, (await fixture.Registry.FindBindingAsync(DatabaseFixture.ParseContext("chat:portal")))!.MemoryProjectId);

        await using var context = fixture.CreateContext();
        var statuses = await context.Database.SqlQueryRaw<string>("SELECT Status AS Value FROM ProjectRoutings ORDER BY MemoryProjectName").ToArrayAsync();
        CollectionAssert.AreEqual(new[] { "Active", "Archived" }, statuses);

        var applied = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
        var available = context.Database.GetMigrations().ToArray();
        Assert.IsNotEmpty(available);
        CollectionAssert.AreEqual(available, applied);
        var sql = await context.Database.SqlQueryRaw<string>("SELECT sql AS Value FROM sqlite_master WHERE name = 'ContextBindings'").SingleAsync();
        Assert.IsTrue(sql.Contains("BINARY", StringComparison.Ordinal));
    }

    /// <summary>
    /// Verifies that upgrading a populated registry preserves exact bindings, IDs and lifecycle states.
    /// </summary>
    /// <returns>The asynchronous migration verification.</returns>
    [TestMethod]
    public async Task UpgradeRetainsProjectsAndBindings()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(migrate: false);
        var id = Guid.NewGuid();
        await using (var legacy = fixture.CreateContext())
        {
            await legacy.GetService<IMigrator>().MigrateAsync("20261121154345_InitialCreate");
            await legacy.Database.ExecuteSqlAsync($"INSERT INTO ProjectRoutings VALUES (1, {id}, 'older', 'Archived', 0, 0)");
            await legacy.Database.ExecuteSqlRawAsync("""
                INSERT INTO ContextBindings VALUES (1,1,'ChatProject','Portal','Active',0,0),(2,1,'ChatProject','portal','Active',0,0)
                """);
        }

        ContextOptions.ClearPool(Path.Combine(fixture.Root, "ProjectMemory.db"));
        await fixture.Services.MigrateDatabaseAsync();
        await using var current = fixture.CreateContext();
        var row = await current.RoutingProjects.SingleAsync();
        Assert.AreEqual(id, row.MemoryProjectId);
        Assert.AreEqual(ProjectStatus.Archived, row.Status);
        Assert.IsFalse(row.DeletionPending);
        foreach (var context in new[] { "chat:Portal", "chat:portal" })
        {
            Assert.AreEqual(id, (await fixture.Registry.FindBindingAsync(DatabaseFixture.ParseContext(context)))!.MemoryProjectId);
        }
    }

    /// <summary>
    /// Verifies that a downgrade cannot discard a committed deletion block.
    /// </summary>
    /// <returns>The asynchronous migration verification.</returns>
    [TestMethod]
    public async Task DowngradePreservesPendingIntent()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(migrate: false);
        await using var db = fixture.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261121165844_ProjectDeletionIntent");
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO ProjectRoutings (MemoryProjectId, MemoryProjectName, Status, CreatedAt, UpdatedAt, DeletionPending)
            VALUES ('00000000-0000-0000-0000-000000000001', 'pending', 'Archived', 0, 0, 1)
            """);

        var appliedBefore = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        await Assert.ThrowsAsync<SqliteException>(() => migrator.MigrateAsync("20261121154345_InitialCreate"));
        Assert.AreEqual(1L, await db.Database.SqlQueryRaw<long>("SELECT DeletionPending AS Value FROM ProjectRoutings").SingleAsync());
        CollectionAssert.AreEqual(appliedBefore, (await db.Database.GetAppliedMigrationsAsync()).ToArray());
    }

    /// <summary>
    /// Verifies that SQLite rejects invalid project and binding updates without changing existing registry state.
    /// </summary>
    /// <param name="invalid">The field to invalidate: project status, context name, context type or binding status.</param>
    /// <returns>A task that completes when the invalid write is rejected and both exact bindings and project statuses remain unchanged.</returns>
    [TestMethod]
    [DataRow("project")]
    [DataRow("context")]
    [DataRow("type")]
    [DataRow("binding")]
    public async Task DatabaseRejectsInvalidRegistryValues(string invalid)
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var a = await fixture.CreateProjectAsync("a");
        var b = await fixture.CreateProjectAsync("b");
        await fixture.Registry.BindAsync(DatabaseFixture.ParseContext("chat:Portal"), a.MemoryProjectId);
        await fixture.Registry.BindAsync(DatabaseFixture.ParseContext("chat:portal"), b.MemoryProjectId);
        await fixture.Registry.SetProjectStatusAsync(b.MemoryProjectId, ProjectStatus.Archived);
        await using (var context = fixture.CreateContext())
        {
            var sql = invalid switch
            {
                "project" => "UPDATE ProjectRoutings SET Status = 'unknown';",
                "context" => "UPDATE ContextBindings SET BindingName = 'invalid context' WHERE Id = 1;",
                "type" => "UPDATE ContextBindings SET BindingType = 'Unknown' WHERE Id = 1;",
                _ => "UPDATE ContextBindings SET Status = 'Unknown' WHERE Id = 1;"
            };

            await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlRawAsync(sql));
        }

        Assert.AreEqual(ProjectStatus.Active, (await fixture.Registry.FindByMemoryProjectIdAsync(a.MemoryProjectId))!.Status);
        Assert.AreEqual(ProjectStatus.Archived, (await fixture.Registry.FindByMemoryProjectIdAsync(b.MemoryProjectId))!.Status);
        Assert.AreEqual(a.MemoryProjectId, (await fixture.Registry.FindBindingAsync(DatabaseFixture.ParseContext("chat:Portal")))!.MemoryProjectId);
        Assert.AreEqual(b.MemoryProjectId, (await fixture.Registry.FindBindingAsync(DatabaseFixture.ParseContext("chat:portal")))!.MemoryProjectId);
        Assert.HasCount(2, await fixture.Registry.ListBindingsPageAsync(null, 0, 10));
    }

    #endregion

    #region Private Methods

    #endregion

    #region Test Classes

    #endregion
}
