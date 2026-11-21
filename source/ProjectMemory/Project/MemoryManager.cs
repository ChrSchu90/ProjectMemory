namespace ProjectMemory.Project;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ProjectMemory.MemoryFiles;
using ProjectMemory.Tools;

/// <summary>
/// Authorizes each file operation under the same project lock as archive, unbind and deletion.
/// </summary>
/// <param name="projects">The exact M2 resolver.</param>
/// <param name="locks">The service's project locks.</param>
/// <param name="checkpoint">Optional observer for coordination tests.</param>
internal sealed class MemoryManager(ProjectManager projects, ProjectLocks locks, Action<LifecycleCheckpoint>? checkpoint = null)
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
    /// Resolves a context then rechecks its active identity inside the filesystem critical section.
    /// </summary>
    /// <param name="context">The exact supplied context ID.</param>
    /// <param name="operation">The bounded operation on the revalidated project.</param>
    /// <param name="token">Cancellation before file access.</param>
    /// <returns>The operation result or a closed validation or routing error.</returns>
    internal async Task<AdministrationResponse<T>> RunAsync<T>(string context, Func<Guid, T> operation, CancellationToken token) where T : class
    {
        var initial = await projects.ResolveAsync(context, token).ConfigureAwait(false);
        if (!initial.Ok)
            return AdministrationResponse<T>.Failure(initial.Error!.Code);
        var id = initial.Data!.ProjectId;
        checkpoint?.Invoke(LifecycleCheckpoint.Routed);
        return await locks.RunAsync(id, async () =>
        {
            var current = await projects.ResolveAsync(context, token).ConfigureAwait(false);
            if (!current.Ok)
                return AdministrationResponse<T>.Failure(current.Error!.Code);
            if (current.Data!.ProjectId != id)
                return AdministrationResponse<T>.Failure("CONFLICT");
            token.ThrowIfCancellationRequested();
            return AdministrationResponse<T>.Success(operation(id));
        }, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates an unprefixed SHA-256 hexadecimal digest.
    /// </summary>
    /// <param name="value">The required expected hash.</param>
    /// <returns>Whether the input contains exactly 64 ASCII hex digits.</returns>
    internal static bool ValidHash(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);

    #endregion

    #region Private Methods

    #endregion
}
