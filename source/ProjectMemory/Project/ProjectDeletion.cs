namespace ProjectMemory.Project;

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProjectMemory.Database;
using ProjectMemory.MemoryFiles;
using ProjectMemory.Routing;
using ProjectMemory.Tools;

/// <summary>
/// Commits an irreversible access block before quarantining a project and finishing the registry cascade.
/// </summary>
/// <param name="factory">The existing SQLite context factory.</param>
/// <param name="locks">The service's project locks.</param>
/// <param name="files">Descriptor-relative project storage.</param>
/// <param name="logger">Logs safe recovery diagnostics.</param>
/// <param name="checkpoint">Optional observer for interruption tests.</param>
internal sealed class ProjectDeletion(IDbContextFactory<ProjectMemoryDbContext> factory, ProjectLocks locks,
    MemoryFileStore files, ILogger<ProjectDeletion> logger, Action<LifecycleCheckpoint>? checkpoint = null)
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
    /// Validates and deletes one explicit active or archived project, including all bindings.
    /// </summary>
    /// <param name="projectId">The exact server-issued UUID.</param>
    /// <param name="token">Cancellation between durable steps.</param>
    /// <returns>The deleted ID or a typed error.</returns>
    internal Task<AdministrationResponse<ProjectDeletedResult>> DeleteAsync(string projectId, CancellationToken token)
    {
        if (!ProjectManager.ValidProjectId(projectId, out var id))
            return Task.FromResult(AdministrationResponse<ProjectDeletedResult>.Failure("INVALID_ARGUMENT"));
        return locks.RunAsync(id, () => DeleteLockedAsync(id, token), token);
    }

    /// <summary>
    /// Resumes only persisted intents before the host accepts requests.
    /// </summary>
    /// <param name="token">Cancellation during startup recovery.</param>
    /// <returns>The task completing after every pending intent has been attempted.</returns>
    internal async Task RecoverAsync(CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token).ConfigureAwait(false);
        var ids = await db.RoutingProjects.Where(row => row.DeletionPending)
            .Select(row => row.MemoryProjectId).ToListAsync(token).ConfigureAwait(false);
        foreach (var id in ids)
        {
            await locks.RunAsync(id, () => DeleteLockedAsync(id, token), token).ConfigureAwait(false);
        }
    }

    #endregion

    #region Private Methods

    private async Task<AdministrationResponse<ProjectDeletedResult>> DeleteLockedAsync(Guid id, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token).ConfigureAwait(false);
        await db.Database.OpenConnectionAsync(token).ConfigureAwait(false);
        // Apply FULL on this connection before the autocommit intent and the final cascade.
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL", token).ConfigureAwait(false);
        var blocked = await db.RoutingProjects.Where(row => row.MemoryProjectId == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, ProjectStatus.Archived)
                .SetProperty(row => row.DeletionPending, true), token).ConfigureAwait(false);
        if (blocked == 0)
            return AdministrationResponse<ProjectDeletedResult>.Failure("PROJECT_NOT_FOUND");
        try
        {
            checkpoint?.Invoke(LifecycleCheckpoint.IntentCommitted);
            token.ThrowIfCancellationRequested();
            files.Purge(id, token);
            var removed = await db.RoutingProjects.Where(row => row.MemoryProjectId == id && row.DeletionPending)
                .ExecuteDeleteAsync(token).ConfigureAwait(false);
            if (removed != 1)
                throw new IOException("Registry cleanup failed.");
            return AdministrationResponse<ProjectDeletedResult>.Success(new ProjectDeletedResult(id));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            logger.LogWarning("Project {ProjectId} requires deletion recovery ({ErrorType}).", id, ex.GetType().Name);
            return AdministrationResponse<ProjectDeletedResult>.Failure("RECOVERY_REQUIRED");
        }
    }

    #endregion
}
