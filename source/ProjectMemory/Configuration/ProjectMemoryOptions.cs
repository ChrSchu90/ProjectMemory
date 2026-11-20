namespace ProjectMemory.Configuration;

using System.Diagnostics;
using Serilog.Events;

/// <summary>
/// Contains the server configuration.
/// </summary>
[DebuggerDisplay("ProjectMemoryOptions")]
public sealed class ProjectMemoryOptions
{
    #region Static Fields

    /// <summary>
    /// Gets the configuration section name.
    /// </summary>
    public const string SectionName = "ProjectMemory";

    /// <summary>
    /// Gets the default minimum Serilog event level.
    /// </summary>
    public const string DefaultLogLevel = nameof(LogEventLevel.Warning);

    #endregion

    #region Private Fields

    #endregion

    #region Constructors

    #endregion

    #region Properties

    /// <summary>
    /// Gets or initializes the minimum Serilog event level.
    /// </summary>
    public string LogLevel { get; init; } = DefaultLogLevel;

    /// <summary>
    /// The directory where the server will store its data, including the SQLite database.
    /// </summary>
    public string DataDirectory { get; init; } = null!;

    #endregion

    #region Public Methods

    #endregion

    #region Private Methods

    #endregion
}
