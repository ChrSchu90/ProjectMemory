namespace ProjectMemory.Hosting;

using System;
using System.IO;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using ProjectMemory.Configuration;

/// <summary>
/// Checks current data-root read/write access without exposing filesystem details.
/// </summary>
/// <param name="options">The validated server configuration.</param>
internal sealed class DataRootHealthCheck(IOptions<ProjectMemoryOptions> options) : IHealthCheck
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

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var root = options.Value.DataDirectory;
            if (!Directory.Exists(root))
                return HealthCheckResult.Unhealthy("Storage unavailable.");

            var path = Path.Combine(root, $".readiness-{Guid.NewGuid():N}");
            await using var probe = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            await probe.WriteAsync(new byte[] { 42 }, cancellationToken);
            probe.Position = 0;
            if (probe.ReadByte() != 42)
                return HealthCheckResult.Unhealthy("Storage unavailable.");

            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return HealthCheckResult.Unhealthy("Storage unavailable.");
        }
    }

    #endregion

    #region Private Methods

    #endregion
}
