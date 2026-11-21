namespace ProjectMemory.Project;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Serializes filesystem operations with changes to the same project's access rights.
/// A fixed set of stripes bounds memory usage across project creation and deletion.
/// </summary>
internal sealed class ProjectLocks : IDisposable
{
    #region Static Fields

    #endregion

    #region Private Fields

    private readonly SemaphoreSlim[] _stripes = Enumerable.Range(0, 128).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    #endregion

    #region Constructors

    #endregion

    #region Properties

    #endregion

    #region Public Methods

    /// <summary>
    /// Runs an operation while holding the stripe for the supplied immutable project ID.
    /// </summary>
    /// <param name="id">The server-owned project identifier.</param>
    /// <param name="operation">The work to perform after acquiring the lock.</param>
    /// <param name="token">Cancellation while waiting for the lock.</param>
    /// <returns>The result produced by the operation.</returns>
    internal async Task<T> RunAsync<T>(Guid id, Func<Task<T>> operation, CancellationToken token)
    {
        var gate = _stripes[(uint)id.GetHashCode() % _stripes.Length];
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var gate in _stripes)
        {
            gate.Dispose();
        }
    }

    #endregion

    #region Private Methods

    #endregion
}
