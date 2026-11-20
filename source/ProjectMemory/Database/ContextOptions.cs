namespace ProjectMemory.Database;

using System;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Creates consistent SQLite options for ProjectMemory database contexts.
/// </summary>
internal static class ContextOptions

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
    /// Creates options for a database file.
    /// </summary>
    /// <param name="databasePath">The absolute SQLite database path.</param>
    /// <param name="pooling">Whether SQLite connection pooling is enabled.</param>
    /// <returns>Configured context options.</returns>
    public static DbContextOptions<ProjectMemoryDbContext> Create(string databasePath, bool pooling)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var optionsBuilder = new DbContextOptionsBuilder<ProjectMemoryDbContext>();
        Configure(optionsBuilder, databasePath, pooling);
        return optionsBuilder.Options;
    }

    /// <summary>
    /// Configures a context options builder for a database file.
    /// </summary>
    /// <param name="optionsBuilder">The options builder to configure.</param>
    /// <param name="databasePath">The absolute SQLite database path.</param>
    /// <param name="pooling">Whether SQLite connection pooling is enabled.</param>
    public static void Configure(DbContextOptionsBuilder optionsBuilder, string databasePath, bool pooling)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = pooling }.ToString();
        optionsBuilder.UseSqlite(connectionString);
    }

    #endregion

    #region Private Methods

    #endregion
}
