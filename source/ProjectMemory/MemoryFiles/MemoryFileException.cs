namespace ProjectMemory.MemoryFiles;

using System.IO;

/// <summary>
/// Carries a fixed contract error for a refused filesystem operation.
/// </summary>
/// <param name="code">The safe, stable contract error code.</param>
internal sealed class MemoryFileException(string code) : IOException(code)
{
    #region Static Fields

    #endregion

    #region Private Fields

    #endregion

    #region Constructors

    #endregion

    #region Properties

    /// <summary>
    /// Gets the contract error code.
    /// </summary>
    internal string Code => Message;

    #endregion

    #region Public Methods

    #endregion

    #region Private Methods

    #endregion
}
