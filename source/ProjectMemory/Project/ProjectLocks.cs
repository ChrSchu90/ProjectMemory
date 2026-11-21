namespace ProjectMemory.Project;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Admits a FIFO queue per project, granting adjacent readers together.
/// </summary>
internal sealed class ProjectLocks
{
    #region Static Fields

    #endregion

    #region Private Fields

    private readonly Lock _sync = new();
    private readonly Dictionary<Guid, ProjectQueue> _projects = [];

    #endregion

    #region Constructors

    #endregion

    #region Properties

    #endregion

    #region Public Methods

    /// <summary>
    /// Runs work after admission and releases its place on completion, cancellation or failure.
    /// </summary>
    /// <param name="id">The server-owned project identifier.</param>
    /// <param name="access">Shared read or exclusive mutation.</param>
    /// <param name="operation">The work to perform after acquiring the lock.</param>
    /// <param name="token">Cancellation while waiting for the lock.</param>
    /// <returns>The result produced by the operation.</returns>
    internal async Task<T> RunAsync<T>(Guid id, ProjectAccess access, Func<Task<T>> operation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ProjectQueue project;
        LinkedListNode<Waiter> node;
        lock (_sync)
        {
            if (!_projects.TryGetValue(id, out var existing))
            {
                existing = new ProjectQueue();
                _projects.Add(id, existing);
            }

            project = existing;
            node = project.Waiting.AddLast(new Waiter(access, new(TaskCreationOptions.RunContinuationsAsynchronously)));
            Admit(project);
        }

        try
        {
            await node.Value.Ready.Task.WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                // A cancellation racing admission owns either a queue node or a grant.
                if (node.List is not null)
                {
                    project.Waiting.Remove(node);
                }
                else
                {
                    project.Active--;
                    project.Writer = false;
                }

                Admit(project);
                if (project.Active == 0)
                {
                    _projects.Remove(id);
                }
            }
        }
    }

    #endregion

    #region Private Methods

    private static void Admit(ProjectQueue project)
    {
        while (!project.Writer && project.Waiting.First is { } next)
        {
            var writer = next.Value.Access == ProjectAccess.Write;
            if (writer && project.Active > 0)
                return;

            project.Writer = writer;
            project.Active++;
            project.Waiting.RemoveFirst();
            next.Value.Ready.SetResult();
        }
    }

    #endregion

    private sealed record Waiter(ProjectAccess Access, TaskCompletionSource Ready);

    private sealed class ProjectQueue
    {
        #region Static Fields

        #endregion

        #region Private Fields

        #endregion

        #region Constructors

        #endregion

        #region Properties

        /// <summary>Gets requests in arrival order.</summary>
        internal LinkedList<Waiter> Waiting { get; } = new();

        /// <summary>Gets or sets the number of outstanding grants.</summary>
        internal int Active { get; set; }

        /// <summary>Gets or sets whether the outstanding grant is exclusive.</summary>
        internal bool Writer { get; set; }

        #endregion

        #region Public Methods

        #endregion

        #region Private Methods

        #endregion
    }
}

/// <summary>Specifies how an operation shares the immutable project's guard.</summary>
internal enum ProjectAccess
{
    /// <summary>May overlap other reads.</summary>
    Read,
    /// <summary>Requires exclusive ownership.</summary>
    Write
}
