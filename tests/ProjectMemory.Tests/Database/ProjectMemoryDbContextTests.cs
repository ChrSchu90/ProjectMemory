namespace ProjectMemory.Tests.Database;

using System.Linq;
using System.Threading.Tasks;
using System;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjectMemory.Database;
using ProjectMemory.Routing;

/// <summary>
/// Tests the <see cref="ProjectMemoryDbContext"/> model, initial schema, persisted enum values and constraints.
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
    /// <returns>A task that completes when the context model is verified against the initial migration.</returns>
    [TestMethod]
    public async Task NoPendingChanges()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(migrate: false);
        await using var context = fixture.CreateContext();
        Assert.IsFalse(context.Database.HasPendingModelChanges());
    }

    /// <summary>
    /// Verifies that the single initial schema persists project identities, framework enum names and exact bindings.
    /// </summary>
    /// <returns>A task that completes when the initial schema, stored statuses and unchanged registry identities are verified.</returns>
    [TestMethod]
    public async Task InitialSchemaPersistsProjectsAndBindings()
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
        Assert.HasCount(1, applied);
        Assert.IsTrue(applied[0].EndsWith("_InitialCreate", StringComparison.Ordinal));
        var sql = await context.Database.SqlQueryRaw<string>("SELECT sql AS Value FROM sqlite_master WHERE name = 'ContextBindings'").SingleAsync();
        Assert.IsTrue(sql.Contains("BINARY", StringComparison.Ordinal));
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
        Assert.HasCount(2, await fixture.Registry.ListBindingsAsync());
    }

    #endregion

    #region Private Methods

    #endregion

    #region Test Classes

    #endregion
}
