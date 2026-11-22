namespace ProjectMemory.Database;

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// Migrates the active SQLite registry only after preserving a validated, recoverable pre-migration snapshot.
/// </summary>
internal sealed class DatabaseMigrator
{
    #region Static Fields

    private static readonly string[] DatabaseSidecarSuffixes = ["-wal", "-shm", "-journal"];

    #endregion

    #region Private Fields

    private readonly ILogger<DatabaseMigrator> _logger;
    private readonly DatabasePaths _paths;

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a database migrator.
    /// </summary>
    /// <param name="paths">The managed database paths.</param>
    /// <param name="logger">The logger.</param>
    public DatabaseMigrator(DatabasePaths paths, ILogger<DatabaseMigrator> logger)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    #endregion

    #region Properties

    #endregion

    #region Public Methods

    /// <summary>
    /// Recovers interrupted migrations before accessing the registry, then brings it to the current schema.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The asynchronous database initialization operation.</returns>
    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        // The marker is a recovery intent, not a cross-process mutex. One live /data owner is required.
        var recovering = File.Exists(_paths.MigrationLockPath);
        if (recovering)
        {
            _logger.LogWarning("Recovering an interrupted lifecycle database migration.");
            await RestorePreMigrationDatabaseAsync(cancellationToken).ConfigureAwait(false);
            DeleteStagingArtifacts();
        }
        else
        {
            // Without recovery intent, never interpret a missing registry with surviving state as a fresh install.
            if (!File.Exists(_paths.DatabasePath) && (File.Exists(_paths.BackupPath) || HasSidecars(_paths.DatabasePath)))
                throw new InvalidOperationException("The active lifecycle database is missing but previous database artifacts exist.");

            DeleteStagingArtifacts();
        }

        if (!File.Exists(_paths.DatabasePath))
        {
            _logger.LogInformation("Creating lifecycle database.");
            await CreateInitialDatabaseAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (new FileInfo(_paths.DatabasePath).Length == 0)
            throw new InvalidOperationException("The active lifecycle database is empty.");

        if (!await HasPendingMigrationsAsync(_paths.DatabasePath, cancellationToken).ConfigureAwait(false))
        {
            if (recovering)
            {
                await ValidateDatabaseAsync(_paths.DatabasePath, cancellationToken).ConfigureAwait(false);
                CompleteMigration();
            }

            _logger.LogDebug("Lifecycle database schema is current.");
            return;
        }

        if (!recovering)
        {
            // Publish a complete backup BEFORE the marker; no migration writes are allowed before both exist.
            await ValidateDatabaseAsync(_paths.DatabasePath, cancellationToken).ConfigureAwait(false);
            try
            {
                CreateStagedBackup();
                await PrepareStandaloneSnapshotAsync(_paths.MigrationPath, cancellationToken).ConfigureAwait(false);
                EnsureNoSidecars(_paths.BackupPath);
                File.Move(_paths.MigrationPath, _paths.BackupPath, overwrite: true);
                await ValidateStandaloneBackupAsync(cancellationToken).ConfigureAwait(false);
                CreateMigrationMarker();
            }
            catch
            {
                TryDeleteStagingArtifacts();
                throw;
            }
        }

        _logger.LogInformation("Applying lifecycle database migrations with a recoverable snapshot.");
        // On failure the marker and immutable backup remain. The next startup restores and retries.
        await ApplyAndValidateMigrationsAsync(_paths.DatabasePath, cancellationToken).ConfigureAwait(false);
        CompleteMigration();
        _logger.LogInformation("Lifecycle database migration completed. Previous database retained.");
    }

    #endregion

    #region Private Methods

    private async Task CreateInitialDatabaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ApplyAndValidateMigrationsAsync(_paths.MigrationPath, cancellationToken).ConfigureAwait(false);
            await SwitchToStandaloneJournalAsync(_paths.MigrationPath, cancellationToken).ConfigureAwait(false);
            EnsureNoSidecars(_paths.MigrationPath);
            ContextOptions.ClearPool(_paths.DatabasePath);
            File.Move(_paths.MigrationPath, _paths.DatabasePath);
        }
        catch
        {
            TryDeleteStagingArtifacts();
            throw;
        }
    }

    private void CreateStagedBackup()
    {
        var sourceConnectionString = new SqliteConnectionStringBuilder { DataSource = _paths.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        var destinationConnectionString = new SqliteConnectionStringBuilder { DataSource = _paths.MigrationPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
        using var sourceConnection = new SqliteConnection(sourceConnectionString);
        using var destinationConnection = new SqliteConnection(destinationConnectionString);
        sourceConnection.Open();
        destinationConnection.Open();
        sourceConnection.BackupDatabase(destinationConnection);
    }

    private static ProjectMemoryDbContext CreateContext(string databasePath)
    {
        var options = ContextOptions.Create(databasePath, pooling: false);
        return new ProjectMemoryDbContext(options);
    }

    private static async Task<bool> HasPendingMigrationsAsync(string databasePath, CancellationToken cancellationToken)
    {
        await using var context = CreateContext(databasePath);
        var pendingMigrations = await context.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false);
        return pendingMigrations.Any();
    }

    private static async Task ApplyAndValidateMigrationsAsync(string databasePath, CancellationToken cancellationToken)
    {
        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

            var pendingMigrations = await context.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false);
            if (pendingMigrations.Any())
                throw new InvalidOperationException("The lifecycle database still has pending EF Core migrations.");
        }

        await ValidateDatabaseAsync(databasePath, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ValidateDatabaseAsync(string databasePath, CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA integrity_check;";
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(Convert.ToString(result, CultureInfo.InvariantCulture), "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"SQLite integrity_check failed for the lifecycle database: {result}");
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_key_check;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("SQLite foreign_key_check failed for the lifecycle database.");
        }
    }

    private async Task PrepareStandaloneSnapshotAsync(string databasePath, CancellationToken cancellationToken)
    {
        // A snapshot must be fully contained in one file before it is published as .pre-migration.
        await SwitchToStandaloneJournalAsync(databasePath, cancellationToken).ConfigureAwait(false);
        await ValidateDatabaseAsync(databasePath, cancellationToken).ConfigureAwait(false);
        EnsureNoSidecars(databasePath);
    }

    private static async Task SwitchToStandaloneJournalAsync(string databasePath, CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=DELETE;";
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(Convert.ToString(result, CultureInfo.InvariantCulture), "delete", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The lifecycle database snapshot could not leave WAL journal mode.");
    }

    private async Task ValidateStandaloneBackupAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.BackupPath))
            throw new InvalidOperationException("The pre-migration snapshot is missing; recovery cannot proceed.");

        // SQLite accepts an empty file as an empty database; it must never qualify as a recovery snapshot.
        if (new FileInfo(_paths.BackupPath).Length < 100)
            throw new InvalidOperationException("The pre-migration snapshot is empty or truncated; recovery cannot proceed.");

        EnsureNoSidecars(_paths.BackupPath);
        await EnsureDeleteJournalModeAsync(_paths.BackupPath, cancellationToken).ConfigureAwait(false);
        await ValidateDatabaseAsync(_paths.BackupPath, cancellationToken).ConfigureAwait(false);
        EnsureNoSidecars(_paths.BackupPath);
    }

    private static async Task EnsureDeleteJournalModeAsync(string databasePath, CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(Convert.ToString(result, CultureInfo.InvariantCulture), "delete", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The pre-migration snapshot is not a standalone DELETE-journal SQLite database.");
    }

    private async Task RestorePreMigrationDatabaseAsync(CancellationToken cancellationToken)
    {
        // Do not touch the active DB until the saved snapshot has passed every check.
        await ValidateStandaloneBackupAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        ContextOptions.ClearPool(_paths.DatabasePath);

        // Only the interrupted active database is discarded. Its WAL/journal belong to that
        // discarded generation and must not be paired with the restored snapshot.
        foreach (var suffix in DatabaseSidecarSuffixes)
        {
            var sidecar = _paths.DatabasePath + suffix;
            if (File.Exists(sidecar))
                File.Delete(sidecar);
        }

        if (File.Exists(_paths.DatabasePath))
            File.Delete(_paths.DatabasePath);

        // A crash during this copy is safe: the marker and the source snapshot remain untouched.
        File.Copy(_paths.BackupPath, _paths.DatabasePath);
        await ValidateDatabaseAsync(_paths.DatabasePath, cancellationToken).ConfigureAwait(false);
    }

    private void CreateMigrationMarker()
    {
        using var marker = new FileStream(_paths.MigrationLockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        marker.Write("migration\n"u8);
        marker.Flush(flushToDisk: true);
    }

    private void CompleteMigration()
    {
        ContextOptions.ClearPool(_paths.DatabasePath);
        File.Delete(_paths.MigrationLockPath);
    }

    private static bool HasSidecars(string databasePath) => DatabaseSidecarSuffixes.Any(suffix => File.Exists(databasePath + suffix));

    private static void EnsureNoSidecars(string databasePath)
    {
        if (HasSidecars(databasePath))
            throw new InvalidOperationException("The lifecycle database snapshot has SQLite journal sidecars and is not standalone.");
    }

    private void DeleteStagingArtifacts()
    {
        foreach (var suffix in DatabaseSidecarSuffixes.Append(string.Empty))
        {
            var file = _paths.MigrationPath + suffix;
            if (File.Exists(file))
                File.Delete(file);
        }
    }

    private void TryDeleteStagingArtifacts()
    {
        try
        {
            ContextOptions.ClearPool(_paths.DatabasePath);
            DeleteStagingArtifacts();
        }
        catch (Exception exception)
        {
            _logger.LogWarning("Failed to remove temporary migration database artifacts ({ErrorType}).", exception.GetType().Name);
        }
    }

    #endregion
}
