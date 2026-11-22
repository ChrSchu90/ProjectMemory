namespace ProjectMemory.Tests.Database;

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjectMemory.Database;

/// <summary>
/// Tests for <see cref="DatabaseMigrator"/>.
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
        ContextOptions.ClearPool(Path.Combine(_testRoot, "ProjectMemory.db"));

        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    #endregion

    #region Tests

    /// <summary>
    /// Verifies that first initialization publishes a complete schema without a backup or recovery marker.
    /// </summary>
    /// <returns>The asynchronous initial database verification.</returns>
    [TestMethod]
    public async Task CreatesCurrentDatabaseWithoutBackup()
    {
        var paths = new DatabasePaths(_testRoot);
        await CreateMigrator(paths).MigrateAsync(CancellationToken.None);
        Assert.IsTrue(File.Exists(paths.DatabasePath));
        Assert.IsFalse(File.Exists(paths.MigrationPath));
        Assert.IsFalse(File.Exists(paths.BackupPath));
        Assert.IsFalse(File.Exists(paths.MigrationLockPath));
        Assert.IsFalse(await HasPendingMigrationsAsync(paths.DatabasePath));
    }

    /// <summary>
    /// Verifies that starting an already current registry neither creates a backup nor a migration marker.
    /// </summary>
    /// <returns>The asynchronous current-schema verification.</returns>
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
        Assert.IsFalse(File.Exists(paths.MigrationLockPath));
    }

    /// <summary>
    /// Verifies that a schema upgrade migrates the active database and retains its original, standalone snapshot.
    /// </summary>
    /// <returns>The asynchronous upgrade and backup verification.</returns>
    [TestMethod]
    public async Task MigratesActiveDatabaseAndPreservesPreviousSnapshot()
    {
        var paths = new DatabasePaths(_testRoot);
        CreateLegacyDatabase(paths.DatabasePath);
        await CreateMigrator(paths).MigrateAsync(CancellationToken.None);
        Assert.IsTrue(File.Exists(paths.DatabasePath));
        Assert.IsTrue(File.Exists(paths.BackupPath));
        Assert.IsFalse(File.Exists(paths.MigrationPath));
        Assert.IsFalse(File.Exists(paths.MigrationLockPath));
        Assert.IsTrue(TableExists(paths.DatabasePath, "LegacyMarker"));
        Assert.IsTrue(TableExists(paths.DatabasePath, "ProjectRoutings"));
        Assert.IsTrue(TableExists(paths.BackupPath, "LegacyMarker"));
        Assert.IsFalse(TableExists(paths.BackupPath, "ProjectRoutings"));
        Assert.IsFalse(File.Exists(paths.BackupPath + "-wal"));
        Assert.IsFalse(File.Exists(paths.BackupPath + "-shm"));
        Assert.IsFalse(File.Exists(paths.BackupPath + "-journal"));
    }

    /// <summary>
    /// Verifies that an abandoned staging file is discarded when first initializing the registry.
    /// </summary>
    /// <returns>The asynchronous interrupted-initialization verification.</returns>
    [TestMethod]
    public async Task StaleMigrationRecreatesDatabase()
    {
        var paths = new DatabasePaths(_testRoot);
        await File.WriteAllTextAsync(paths.MigrationPath, "stale");
        await CreateMigrator(paths).MigrateAsync(CancellationToken.None);
        Assert.IsTrue(File.Exists(paths.DatabasePath));
        Assert.IsFalse(File.Exists(paths.MigrationPath));
        Assert.IsFalse(File.Exists(paths.MigrationLockPath));
        Assert.IsFalse(await HasPendingMigrationsAsync(paths.DatabasePath));
    }

    /// <summary>
    /// Verifies that a failed migration retains a valid backup and the recovery marker for a later restart.
    /// </summary>
    /// <returns>The asynchronous failed-migration recovery verification.</returns>
    [TestMethod]
    public async Task FailedMigrationRetainsSnapshotAndRecoveryMarker()
    {
        var paths = new DatabasePaths(_testRoot);
        using (var connection = OpenConnection(paths.DatabasePath, SqliteOpenMode.ReadWriteCreate))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE ProjectRoutings (Id INTEGER PRIMARY KEY, Marker TEXT NOT NULL); INSERT INTO ProjectRoutings VALUES (1, 'preserve');";
            command.ExecuteNonQuery();
        }

        var migrator = CreateMigrator(paths);
        await Assert.ThrowsAsync<SqliteException>(() => migrator.MigrateAsync(CancellationToken.None));
        Assert.IsTrue(File.Exists(paths.MigrationLockPath));
        Assert.IsTrue(File.Exists(paths.BackupPath));
        Assert.IsFalse(File.Exists(paths.MigrationPath));
        Assert.AreEqual("preserve", ReadTextValue(paths.BackupPath, "SELECT Marker FROM ProjectRoutings WHERE Id = 1"));
        var backupBytes = await File.ReadAllBytesAsync(paths.BackupPath);

        // A second start first restores the snapshot, then retries the same invalid migration.
        await Assert.ThrowsAsync<SqliteException>(() => migrator.MigrateAsync(CancellationToken.None));
        Assert.IsTrue(File.Exists(paths.MigrationLockPath));
        CollectionAssert.AreEqual(backupBytes, await File.ReadAllBytesAsync(paths.BackupPath));
        Assert.AreEqual("preserve", ReadTextValue(paths.DatabasePath, "SELECT Marker FROM ProjectRoutings WHERE Id = 1"));
    }

    /// <summary>
    /// Verifies that an interrupted schema migration restores the previous rows before retrying it.
    /// </summary>
    /// <returns>The asynchronous interrupted-migration recovery verification.</returns>
    [TestMethod]
    public async Task RecoversInterruptedMigrationBeforeRetrying()
    {
        var paths = new DatabasePaths(_testRoot);
        CreateLegacyDatabase(paths.DatabasePath);
        PrepareInterruptedMigration(paths);
        using (var connection = OpenConnection(paths.DatabasePath, SqliteOpenMode.ReadWrite))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TABLE LegacyMarker;";
            command.ExecuteNonQuery();
        }

        var before = await File.ReadAllBytesAsync(paths.BackupPath);
        await CreateMigrator(paths).MigrateAsync(CancellationToken.None);
        Assert.IsTrue(TableExists(paths.DatabasePath, "LegacyMarker"));
        Assert.IsTrue(TableExists(paths.DatabasePath, "ProjectRoutings"));
        Assert.IsFalse(File.Exists(paths.MigrationLockPath));
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(paths.BackupPath));
    }

    /// <summary>
    /// Verifies that an interrupted restore can be repeated without ever overwriting its backup source.
    /// </summary>
    /// <returns>The asynchronous repeatable-restore verification.</returns>
    [TestMethod]
    public async Task InterruptedRestoreCanBeRepeatedWithoutChangingBackup()
    {
        var paths = new DatabasePaths(_testRoot);
        CreateLegacyDatabase(paths.DatabasePath);
        PrepareInterruptedMigration(paths);
        var snapshot = await File.ReadAllBytesAsync(paths.BackupPath);
        var migrator = CreateMigrator(paths);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            // Represents a crash after the old database was deleted or during File.Copy.
            File.WriteAllText(paths.DatabasePath, "partial restore");
            File.WriteAllText(paths.DatabasePath + "-wal", "interrupted wal");
            File.WriteAllText(paths.DatabasePath + "-shm", "interrupted shm");
            File.WriteAllText(paths.DatabasePath + "-journal", "interrupted journal");
            await migrator.MigrateAsync(CancellationToken.None);
            Assert.IsFalse(File.Exists(paths.MigrationLockPath));
            Assert.IsFalse(File.Exists(paths.DatabasePath + "-wal"));
            Assert.IsFalse(File.Exists(paths.DatabasePath + "-shm"));
            Assert.IsFalse(File.Exists(paths.DatabasePath + "-journal"));
            CollectionAssert.AreEqual(snapshot, await File.ReadAllBytesAsync(paths.BackupPath));
            Assert.IsTrue(TableExists(paths.DatabasePath, "ProjectRoutings"));

            if (attempt == 0)
                File.WriteAllText(paths.MigrationLockPath, "interrupted");
        }
    }

    /// <summary>
    /// Verifies that a recovery marker without its snapshot cannot erase the active registry.
    /// </summary>
    /// <returns>The asynchronous missing-snapshot rejection verification.</returns>
    [TestMethod]
    public async Task MissingSnapshotBlocksRecoveryWithoutChangingActiveDatabase()
    {
        var paths = new DatabasePaths(_testRoot);
        CreateLegacyDatabase(paths.DatabasePath);
        File.WriteAllText(paths.MigrationLockPath, "interrupted");
        var before = await File.ReadAllBytesAsync(paths.DatabasePath);

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateMigrator(paths).MigrateAsync(CancellationToken.None));
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(paths.DatabasePath));
        Assert.IsTrue(File.Exists(paths.MigrationLockPath));
    }

    /// <summary>
    /// Verifies that a corrupt snapshot cannot overwrite or erase an otherwise intact database.
    /// </summary>
    /// <returns>The asynchronous invalid-snapshot rejection verification.</returns>
    [TestMethod]
    public async Task CorruptSnapshotBlocksRecoveryWithoutChangingActiveDatabase()
    {
        var paths = new DatabasePaths(_testRoot);
        CreateLegacyDatabase(paths.DatabasePath);
        File.WriteAllText(paths.BackupPath, "invalid sqlite file");
        File.WriteAllText(paths.MigrationLockPath, "interrupted");
        var before = await File.ReadAllBytesAsync(paths.DatabasePath);

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateMigrator(paths).MigrateAsync(CancellationToken.None));
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(paths.DatabasePath));
        Assert.IsTrue(File.Exists(paths.MigrationLockPath));
    }

    /// <summary>
    /// Verifies that an empty SQLite file is not accepted as a valid pre-migration recovery source.
    /// </summary>
    /// <returns>The asynchronous empty-snapshot rejection verification.</returns>
    [TestMethod]
    public async Task EmptySnapshotCannotReplaceActiveDatabase()
    {
        var paths = new DatabasePaths(_testRoot);
        CreateLegacyDatabase(paths.DatabasePath);
        File.WriteAllBytes(paths.BackupPath, []);
        File.WriteAllText(paths.MigrationLockPath, "interrupted");
        var before = await File.ReadAllBytesAsync(paths.DatabasePath);

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateMigrator(paths).MigrateAsync(CancellationToken.None));
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(paths.DatabasePath));
        Assert.IsTrue(File.Exists(paths.MigrationLockPath));
    }

    /// <summary>
    /// Verifies that a recovery marker left after completed schema migration still restores before removing it.
    /// </summary>
    /// <returns>The asynchronous current-schema recovery verification.</returns>
    [TestMethod]
    public async Task RecoveryMarkerWithCurrentSchemaStillRestoresSnapshot()
    {
        var paths = new DatabasePaths(_testRoot);
        await CreateMigrator(paths).MigrateAsync(CancellationToken.None);
        File.Copy(paths.DatabasePath, paths.BackupPath);
        File.WriteAllText(paths.MigrationLockPath, "interrupted");
        File.WriteAllText(paths.DatabasePath, "partial");

        await CreateMigrator(paths).MigrateAsync(CancellationToken.None);
        Assert.IsFalse(File.Exists(paths.MigrationLockPath));
        Assert.IsFalse(await HasPendingMigrationsAsync(paths.DatabasePath));
        Assert.IsTrue(TableExists(paths.DatabasePath, "ProjectRoutings"));
    }

    /// <summary>
    /// Verifies that an absent active database with a previous snapshot is not mistaken for an empty installation.
    /// </summary>
    /// <returns>The asynchronous missing-active-database rejection verification.</returns>
    [TestMethod]
    public async Task MissingActiveDatabaseWithPreviousSnapshotFailsClosed()
    {
        var paths = new DatabasePaths(_testRoot);
        CreateLegacyDatabase(paths.DatabasePath);
        File.Copy(paths.DatabasePath, paths.BackupPath);
        File.Delete(paths.DatabasePath);
        var snapshot = await File.ReadAllBytesAsync(paths.BackupPath);

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateMigrator(paths).MigrateAsync(CancellationToken.None));
        Assert.IsFalse(File.Exists(paths.DatabasePath));
        CollectionAssert.AreEqual(snapshot, await File.ReadAllBytesAsync(paths.BackupPath));
    }

    /// <summary>
    /// Verifies that an interrupted restore can restart even when the active database was already deleted.
    /// </summary>
    /// <returns>The asynchronous missing-active-database recovery verification.</returns>
    [TestMethod]
    public async Task RecoveryRestoresMissingActiveDatabaseFromSnapshot()
    {
        var paths = new DatabasePaths(_testRoot);
        CreateLegacyDatabase(paths.DatabasePath);
        PrepareInterruptedMigration(paths);
        var snapshot = await File.ReadAllBytesAsync(paths.BackupPath);
        File.Delete(paths.DatabasePath);

        await CreateMigrator(paths).MigrateAsync(CancellationToken.None);
        Assert.IsFalse(File.Exists(paths.MigrationLockPath));
        Assert.IsTrue(TableExists(paths.DatabasePath, "LegacyMarker"));
        Assert.IsTrue(TableExists(paths.DatabasePath, "ProjectRoutings"));
        CollectionAssert.AreEqual(snapshot, await File.ReadAllBytesAsync(paths.BackupPath));
    }

    /// <summary>
    /// Verifies that a previous backup with a journal sidecar cannot be silently overwritten by a new snapshot.
    /// </summary>
    /// <returns>The asynchronous unsafe-backup-state rejection verification.</returns>
    [TestMethod]
    public async Task ExistingBackupSidecarBlocksNewMigrationWithoutTouchingActiveDatabase()
    {
        var paths = new DatabasePaths(_testRoot);
        CreateLegacyDatabase(paths.DatabasePath);
        File.WriteAllText(paths.BackupPath + "-wal", "unpaired journal");
        var original = await File.ReadAllBytesAsync(paths.DatabasePath);

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateMigrator(paths).MigrateAsync(CancellationToken.None));
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(paths.DatabasePath));
        Assert.IsTrue(File.Exists(paths.BackupPath + "-wal"));
        Assert.IsFalse(File.Exists(paths.MigrationLockPath));
        Assert.IsFalse(File.Exists(paths.MigrationPath));
    }

    /// <summary>
    /// Verifies that a missing main database with a surviving WAL does not trigger creation of a blank registry.
    /// </summary>
    /// <returns>The asynchronous orphaned-WAL rejection verification.</returns>
    [TestMethod]
    public async Task MissingActiveDatabaseWithOrphanedWalFailsClosed()
    {
        var paths = new DatabasePaths(_testRoot);
        File.WriteAllText(paths.DatabasePath + "-wal", "orphaned");
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateMigrator(paths).MigrateAsync(CancellationToken.None));
        Assert.IsFalse(File.Exists(paths.DatabasePath));
        Assert.IsTrue(File.Exists(paths.DatabasePath + "-wal"));
    }

    /// <summary>
    /// Verifies that an existing WAL-mode registry can be backed up with its committed data before upgrading.
    /// </summary>
    /// <returns>The asynchronous WAL-backed snapshot verification.</returns>
    [TestMethod]
    public async Task WalBackedDatabaseCreatesStandaloneSnapshot()
    {
        var paths = new DatabasePaths(_testRoot);
        CreateLegacyDatabase(paths.DatabasePath);
        using (var connection = OpenConnection(paths.DatabasePath, SqliteOpenMode.ReadWrite))
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA journal_mode=WAL;";
                Assert.AreEqual("wal", Convert.ToString(command.ExecuteScalar()));
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "INSERT INTO LegacyMarker (Id) VALUES (2);";
                command.ExecuteNonQuery();
            }

            await CreateMigrator(paths).MigrateAsync(CancellationToken.None);
            Assert.AreEqual("2", ReadTextValue(paths.BackupPath, "SELECT COUNT(*) FROM LegacyMarker"));
            Assert.IsFalse(File.Exists(paths.BackupPath + "-wal"));
            Assert.IsFalse(File.Exists(paths.BackupPath + "-shm"));
        }

        Assert.IsFalse(File.Exists(paths.MigrationLockPath));
        Assert.IsTrue(TableExists(paths.DatabasePath, "ProjectRoutings"));
    }

    /// <summary>
    /// Verifies that a stale temporary backup is discarded before a fresh migration snapshot is produced.
    /// </summary>
    /// <returns>The asynchronous staging-cleanup verification.</returns>
    [TestMethod]
    public async Task StaleStagingIsDiscardedBeforeCreatingSnapshot()
    {
        var paths = new DatabasePaths(_testRoot);
        CreateLegacyDatabase(paths.DatabasePath);
        File.WriteAllText(paths.MigrationPath, "incomplete snapshot");
        await CreateMigrator(paths).MigrateAsync(CancellationToken.None);
        Assert.IsTrue(TableExists(paths.BackupPath, "LegacyMarker"));
        Assert.IsFalse(File.Exists(paths.MigrationPath));
        Assert.IsFalse(File.Exists(paths.MigrationLockPath));
    }

    #endregion

    #region Private Methods

    private static DatabaseMigrator CreateMigrator(DatabasePaths paths) => new(paths, NullLogger<DatabaseMigrator>.Instance);

    private static ProjectMemoryDbContext CreateContext(string databasePath) => new(ContextOptions.Create(databasePath, pooling: false));

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

    private static void PrepareInterruptedMigration(DatabasePaths paths)
    {
        File.Copy(paths.DatabasePath, paths.BackupPath);
        File.WriteAllText(paths.MigrationLockPath, "interrupted");
    }

    private static bool TableExists(string databasePath, string tableName)
    {
        using var connection = OpenConnection(databasePath, SqliteOpenMode.ReadOnly);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }

    private static string ReadTextValue(string databasePath, string statement)
    {
        using var connection = OpenConnection(databasePath, SqliteOpenMode.ReadOnly);
        using var command = connection.CreateCommand();
        command.CommandText = statement;
        return Convert.ToString(command.ExecuteScalar())!;
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
