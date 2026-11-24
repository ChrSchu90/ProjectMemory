namespace ProjectMemory.Database.Routing;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ProjectMemory.Database.Entities;
using ProjectMemory.Routing;

/// <summary>
/// Stores memory project lifecycle metadata in the EF Core database.
/// </summary>
internal sealed class EfProjectRegistry : IProjectRegistry
{
    #region Static Fields

    private const int SqliteUniqueConstraintErrorCode = 2067;
    private const int SqliteConstraintErrorCode = 19;

    #endregion

    #region Private Fields

    private readonly IDbContextFactory<ProjectMemoryDbContext> _dbContextFactory;

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="EfProjectRegistry"/> class.
    /// </summary>
    /// <param name="dbContextFactory">The database context factory.</param>
    /// <exception cref="ArgumentNullException">dbContextFactory</exception>
    public EfProjectRegistry(IDbContextFactory<ProjectMemoryDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
    }

    #endregion

    #region Properties

    #endregion

    #region Public Methods

    /// <inheritdoc />
    public async Task<ProjectRoute?> FindRouteAsync(ContextId contextId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contextId);

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var route = await dbContext.ContextBindings
                        .AsNoTracking()
                        .Where(binding => binding.BindingType == contextId.BindingType && binding.BindingName == contextId.BindingName)
                        .Select(binding => new ProjectRoute(binding.RoutingProject.MemoryProjectId, binding.RoutingProject.MemoryProjectName, binding.RoutingProject.DeletionPending ? ProjectStatus.Archived : binding.RoutingProject.Status, binding.Status))
                        .SingleOrDefaultAsync(cancellationToken)
                        .ConfigureAwait(false);

        return route;
    }

    /// <inheritdoc />
    public async Task<ProjectRouting?> FindByMemoryProjectIdAsync(Guid memoryProjectId, CancellationToken cancellationToken = default)
    {
        if (memoryProjectId == Guid.Empty)
            throw new ArgumentException("The project identifier must not be empty.", nameof(memoryProjectId));

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await dbContext.RoutingProjects
            .AsNoTracking()
            .Where(project => project.MemoryProjectId == memoryProjectId)
            .Select(project => new ProjectRouting(project.MemoryProjectId, project.MemoryProjectName, project.Status))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ProjectRouting> CreateAsync(Guid memoryProjectId, string memoryProjectName, ProjectStatus status, CancellationToken cancellationToken = default)
    {
        if (memoryProjectId == Guid.Empty)
            throw new ArgumentException("The project identifier must not be empty.", nameof(memoryProjectId));

        ArgumentException.ThrowIfNullOrWhiteSpace(memoryProjectName);

        if (status is not ProjectStatus.Active and not ProjectStatus.Archived)
            throw new ArgumentOutOfRangeException(nameof(status), status, "The project routing status is unsupported.");

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = new ProjectRoutingEntity
        {
            MemoryProjectId = memoryProjectId,
            MemoryProjectName = memoryProjectName,
            Status = status
        };

        dbContext.RoutingProjects.Add(entity);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            throw new ProjectRegistryConflictException("A project routing with the same project identifier or name already exists.", ex);
        }

        return new ProjectRouting(entity.MemoryProjectId, entity.MemoryProjectName, entity.Status);
    }

    /// <inheritdoc />
    public async Task<ContextBinding?> FindBindingAsync(ContextId contextId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contextId);
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await dbContext.ContextBindings
                   .AsNoTracking()
                   .Where(binding => binding.BindingType == contextId.BindingType && binding.BindingName == contextId.BindingName)
                   .Select(binding => new ContextBinding(contextId, binding.RoutingProject.MemoryProjectId, binding.Status))
                   .SingleOrDefaultAsync(cancellationToken)
                   .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> TryRemoveBindingAsync(ContextId contextId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contextId);

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var affectedRows = await dbContext.ContextBindings
                               .Where(binding => binding.BindingType == contextId.BindingType && binding.BindingName == contextId.BindingName && !binding.RoutingProject.DeletionPending)
                               .ExecuteDeleteAsync(cancellationToken)
                               .ConfigureAwait(false);

        switch (affectedRows)
        {
            case 0:
                return false;
            case 1:
                return true;
            default:
                throw new InvalidOperationException($"Removing context binding '{contextId}' affected more than one row.");
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProjectRouting>> ListProjectsPageAsync(int offset, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.RoutingProjects.AsNoTracking().OrderBy(project => project.MemoryProjectName).ThenBy(project => project.MemoryProjectId)
            .Skip(offset).Take(limit).Select(project => new ProjectRouting(project.MemoryProjectId, project.MemoryProjectName, project.Status)).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ContextBinding>> ListBindingsPageAsync(Guid? projectId, int offset, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = context.ContextBindings.AsNoTracking();
        if (projectId.HasValue)
        {
            query = query.Where(binding => binding.RoutingProject.MemoryProjectId == projectId.Value);
        }

        var rows = await query.OrderBy(binding => binding.BindingType).ThenBy(binding => binding.BindingName).ThenBy(binding => binding.Id)
            .Skip(offset).Take(limit).Select(binding => new { binding.BindingType, binding.BindingName, binding.Status, binding.RoutingProject.MemoryProjectId }).ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows.Select(row => new ContextBinding(CreateContextId(row.BindingType, row.BindingName), row.MemoryProjectId, row.Status)).ToArray();
    }

    /// <inheritdoc />
    public async Task<RegistryChange<ContextBinding>> BindAsync(ContextId contextId, Guid projectId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contextId);
        if (projectId == Guid.Empty)
            throw new ArgumentException("A project identifier is required.", nameof(projectId));
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        // Microsoft.Data.Sqlite starts a non-deferred transaction: reserve the writer before checking state.
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var project = await context.RoutingProjects.SingleOrDefaultAsync(item => item.MemoryProjectId == projectId, cancellationToken).ConfigureAwait(false);
        if (project is null)
            return new(null, Error: RegistryError.ProjectNotFound);
        if (project.DeletionPending)
            return new(null, Error: RegistryError.ProjectBlocked);
        if (project.Status != ProjectStatus.Active)
            return new(null, Error: RegistryError.ProjectArchived);
        var existing = await context.ContextBindings.SingleOrDefaultAsync(binding => binding.BindingType == contextId.BindingType && binding.BindingName == contextId.BindingName, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.RoutingProjectId != project.Id || existing.Status != Status.Active)
                return new(null, Error: RegistryError.Conflict);
            return new(new ContextBinding(contextId, projectId, Status.Active));
        }

        context.ContextBindings.Add(new ContextBindingEntity { RoutingProjectId = project.Id, BindingType = contextId.BindingType, BindingName = contextId.BindingName });

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(new ContextBinding(contextId, projectId, Status.Active), Changed: true);
    }

    /// <inheritdoc />
    public async Task<RegistryChange<ProjectRouting>> SetProjectStatusAsync(Guid projectId, ProjectStatus status, CancellationToken cancellationToken = default)
    {
        if (projectId == Guid.Empty)
            throw new ArgumentException("A project identifier is required.", nameof(projectId));
        if (status is not ProjectStatus.Active and not ProjectStatus.Archived)
            throw new ArgumentOutOfRangeException(nameof(status));
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var project = await context.RoutingProjects.SingleOrDefaultAsync(item => item.MemoryProjectId == projectId, cancellationToken).ConfigureAwait(false);
        if (project is null)
            return new(null, Error: RegistryError.ProjectNotFound);
        if (project.DeletionPending)
            return new(null, Error: RegistryError.ProjectBlocked);
        var changed = project.Status != status;
        if (changed)
        {
            project.Status = status;
            project.UpdatedAt = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(new ProjectRouting(project.MemoryProjectId, project.MemoryProjectName, project.Status), changed);
    }

    #endregion

    #region Private Methods

    private static ContextId CreateContextId(BindingType bindingType, string bindingName)
    {
        try
        {
            return ContextId.Create(bindingType, bindingName);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException($"Persisted context binding type '{bindingType}' and name '{bindingName}' are invalid.", exception);
        }
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException exception)
    {
        return exception.InnerException is SqliteException
        {
            SqliteErrorCode: SqliteConstraintErrorCode,
            SqliteExtendedErrorCode: SqliteUniqueConstraintErrorCode
        };
    }

    #endregion
}
