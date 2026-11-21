namespace ProjectMemory.Tests.Database;

using System.IO;
using System.Threading.Tasks;
using System;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProjectMemory.Configuration;
using ProjectMemory.Database;
using ProjectMemory.Project;
using ProjectMemory.Routing;

/// <summary>
/// Owns isolated storage and services for tests that require a real SQLite database.
/// </summary>
/// <param name="root">The disposable directory containing the test database.</param>
/// <param name="services">The service provider owned and disposed by this fixture.</param>
internal sealed class DatabaseFixture(string root, ServiceProvider services) : IAsyncDisposable
{
    #region Static Fields

    #endregion

    #region Private Fields

    #endregion

    #region Constructors

    #endregion

    #region Properties

    /// <summary>
    /// Gets the disposable data directory owned by this fixture.
    /// </summary>
    public string Root => root;

    /// <summary>
    /// Gets the service provider owned by this fixture.
    /// </summary>
    public ServiceProvider Services => services;

    /// <summary>
    /// Gets the SQLite registry registered in the fixture's service provider.
    /// </summary>
    public IProjectRegistry Registry => services.GetRequiredService<IProjectRegistry>();

    #endregion

    #region Public Methods

    /// <summary>
    /// Parses a canonical context key used to arrange isolated database test data.
    /// </summary>
    /// <param name="value">The complete exact context ID to validate.</param>
    /// <returns>The validated context ID with its original case preserved.</returns>
    internal static ContextId ParseContext(string value)
    {
        if (!ContextId.TryParse(value, out var contextId))
            throw new ArgumentException("A valid context ID is required for test data.", nameof(value));

        return contextId;
    }

    /// <summary>
    /// Creates an isolated registry fixture and optionally initializes its database schema.
    /// </summary>
    /// <param name="migrate">Whether to apply the current migrations before returning the fixture.</param>
    /// <returns>A task containing the fixture that the caller must dispose after the test.</returns>
    public static async Task<DatabaseFixture> CreateAsync(bool migrate = true)
    {
        var root = Path.Combine(Path.GetTempPath(), "ProjectMemory.Tests", "Registry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var collection = new ServiceCollection().AddLogging();
        collection.AddSingleton(Options.Create(new ProjectMemoryOptions { DataDirectory = root }));

        collection.AddPersistence();
        collection.AddSingleton<ProjectManager>();
        var services = collection.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        var fixture = new DatabaseFixture(root, services);
        try
        {
            if (migrate)
            {
                await services.MigrateDatabaseAsync();
            }

            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Creates an active test project with a new immutable identifier.
    /// </summary>
    /// <param name="name">The project name to register in the isolated database.</param>
    /// <returns>A task containing the newly persisted project.</returns>
    public Task<ProjectRouting> CreateProjectAsync(string name) => Registry.CreateAsync(Guid.NewGuid(), name, ProjectStatus.Active);

    /// <summary>
    /// Opens a context configured for the fixture's database without connection pooling.
    /// </summary>
    /// <returns>A database context that the caller must dispose.</returns>
    public ProjectMemoryDbContext CreateContext() => new(ContextOptions.Create(Path.Combine(Root, "ProjectMemory.db"), pooling: false));

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(Root, recursive: true);
    }

    #endregion

    #region Private Methods

    #endregion
}
