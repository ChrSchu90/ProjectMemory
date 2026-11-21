namespace ProjectMemory.Tests.Database;

using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjectMemory.Database;

/// <summary>
/// Tests for <see cref="DatabaseMigrator"/>
/// </summary>
[TestClass]
public sealed class DatabaseMigratorTests
{
    #region Static Fields

    #endregion

    #region Private Fields

    private string _testRoot = null!;

    #endregion

    #region Test Constructors

    #endregion

    #region Test Preparation

    /// <summary>
    /// Creates a unique temporary directory for the migration test.
    /// </summary>
    [TestInitialize]
    public void Initialize()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "ProjectMemory.Tests", Path.GetRandomFileName());
        Directory.CreateDirectory(_testRoot);
    }

    /// <summary>
    /// Releases SQLite pools and removes the temporary test directory.
    /// </summary>
    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();

        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    #endregion

    #region Tests

    /// <summary>
    /// Verifies that migrating without an existing database creates a current database without leaving migration or backup artifacts.
    /// </summary>
    /// <returns>A task that completes when the test has verified that migrating without an existing database creates a current database without leaving migration or backup artifacts.</returns>
    [TestMethod]
    public async Task CreatesCurrentDatabaseWithoutBackup()
    {
        var paths = new DatabasePaths(_testRoot);
        var migrator = CreateMigrator(paths);
        await migrator.MigrateAsync(CancellationToken.None);
        Assert.IsTrue(File.Exists(paths.DatabasePath));
        Assert.IsFalse(File.Exists(paths.MigrationPath));
        Assert.IsFalse(File.Exists(paths.BackupPath));
        Assert.IsFalse(await HasPendingMigrationsAsync(paths.DatabasePath));
    }

    /// <summary>
    /// Verifies that rerunning migration on an already current database does not create migration or backup artifacts.
    /// </summary>
    /// <returns>A task that completes when the test has verified that rerunning migration on an already current database does not create migration or backup artifacts.</returns>
    [TestMethod]
    public async Task DoesNotCreateBackupWithoutMigrating()
    {
        var paths = new DatabasePaths(_testRoot);
        var migrator = CreateMigrator(paths);
        await migrator.MigrateAsync(CancellationToken.None);
        await migrator.MigrateAsync(CancellationToken.None);
        Assert.IsTrue(File.Exists(paths.DatabasePath));
        Assert.IsFalse(File.Exists(paths.MigrationPath));
        Assert.IsFalse(File.Exists(paths.BackupPath));
    }

    /// <summary>
    /// Verifies that an existing database requiring migration is migrated on a copy, activated successfully, and preserves the original database as a backup.
    /// </summary>
    /// <returns>A task that completes when the test has verified that an existing database requiring migration is migrated on a copy, activated successfully, and preserves the original database as a backup.</returns>
    [TestMethod]
    public async Task MigrationCopyAndBacksUpOriginal()
    {
        var paths = new DatabasePaths(_testRoot);
        CreateLegacyDatabase(paths.DatabasePath);
        var migrator = CreateMigrator(paths);
        await migrator.MigrateAsync(CancellationToken.None);
        Assert.IsTrue(File.Exists(paths.DatabasePath));
        Assert.IsTrue(File.Exists(paths.BackupPath));
        Assert.IsFalse(File.Exists(paths.MigrationPath));
        Assert.IsTrue(TableExists(paths.DatabasePath, "LegacyMarker"));
        Assert.IsTrue(TableExists(paths.DatabasePath, "ProjectRoutings"));
        Assert.IsTrue(TableExists(paths.BackupPath, "LegacyMarker"));
        Assert.IsFalse(TableExists(paths.BackupPath, "ProjectRoutings"));
    }

    /// <summary>
    /// Verifies that a stale temporary migration database is discarded before creating a valid current database when no active database exists.
    /// </summary>
    /// <returns>A task that completes when the test has verified that a stale temporary migration database is discarded before creating a valid current database when no active database exists.</returns>
    [TestMethod]
    public async Task StaleMigrationRecreatesDatabase()
    {
        var paths = new DatabasePaths(_testRoot);
        await File.WriteAllTextAsync(paths.MigrationPath, "stale");
        var migrator = CreateMigrator(paths);
        await migrator.MigrateAsync(CancellationToken.None);
        Assert.IsTrue(File.Exists(paths.DatabasePath));
        Assert.IsFalse(File.Exists(paths.MigrationPath));
        Assert.IsFalse(await HasPendingMigrationsAsync(paths.DatabasePath));
    }

    /// <summary>
    /// Verifies that failure to initialize a conflicting database leaves the original bytes intact and removes the migration copy.
    /// </summary>
    /// <returns>A task that completes when the failed initial migration preserves the original database without publishing a replacement.</returns>
    [TestMethod]
    public async Task FailedInitialMigrationKeepsOriginalDatabase()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(migrate: false);
        await using (var context = fixture.CreateContext())
        {
            await context.Database.ExecuteSqlRawAsync("CREATE TABLE ProjectRoutings (Id INTEGER PRIMARY KEY, Marker TEXT NOT NULL); INSERT INTO ProjectRoutings VALUES (1, 'preserve');");
        }

        SqliteConnection.ClearAllPools();
        var path = Path.Combine(fixture.Root, "ProjectMemory.db");
        var before = await File.ReadAllBytesAsync(path);
        await Assert.ThrowsAsync<SqliteException>(() => fixture.Services.MigrateDatabaseAsync());
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(path));
        Assert.IsFalse(File.Exists(path + ".migrate"));
    }

    #endregion

    #region Private Methods

    private static DatabaseMigrator CreateMigrator(DatabasePaths paths)
    {
        return new DatabaseMigrator(paths, NullLogger<DatabaseMigrator>.Instance);
    }

    private static ProjectMemoryDbContext CreateContext(string databasePath)
    {
        return new ProjectMemoryDbContext(ContextOptions.Create(databasePath, pooling: false));
    }

    private static async Task<bool> HasPendingMigrationsAsync(string databasePath)
    {
        await using var context = CreateContext(databasePath);
        var pendingMigrations = await context.Database.GetPendingMigrationsAsync();
        return pendingMigrations.Any();
    }

    private static void CreateLegacyDatabase(string databasePath)
    {
        using var connection = OpenConnection(databasePath, SqliteOpenMode.ReadWriteCreate);
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE LegacyMarker (Id INTEGER NOT NULL PRIMARY KEY); INSERT INTO LegacyMarker (Id) VALUES (1);";
        command.ExecuteNonQuery();
    }

    private static bool TableExists(string databasePath, string tableName)
    {
        using var connection = OpenConnection(databasePath, SqliteOpenMode.ReadOnly);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }

    private static SqliteConnection OpenConnection(string databasePath, SqliteOpenMode mode)
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = mode, Pooling = false }.ToString();
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    #endregion

    #region Test Classes

    #endregion
}
