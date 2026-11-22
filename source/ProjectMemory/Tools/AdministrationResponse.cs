namespace ProjectMemory.Tools;

/// <summary>
/// Defines the versioned administration result shared by JSON and MCP text content.
/// </summary>
/// <typeparam name="TData">The operation-specific data contract serialized on success.</typeparam>
/// <param name="ContractVersion">The response contract version supplied by the response factories.</param>
/// <param name="Ok">Whether the operation succeeded.</param>
/// <param name="Data">The successful operation's payload.</param>
/// <param name="Error">An expected failure, or null on success.</param>
internal sealed record AdministrationResponse<TData>(string ContractVersion, bool Ok, TData? Data, AdministrationError? Error) where TData : class
{
    #region Static Fields

    /// <summary>The version shared by every MCP response and its published output schema.</summary>
    internal const string CurrentContractVersion = "1.0";

    #endregion

    #region Private Fields

    #endregion

    #region Constructors

    #endregion

    #region Properties

    #endregion

    #region Public Methods

    /// <summary>
    /// Creates a successful structured response.
    /// </summary>
    /// <param name="data">The operation-specific payload to include in the response.</param>
    /// <returns>A successful response containing the supplied payload and no error.</returns>
    public static AdministrationResponse<TData> Success(TData data) => new(CurrentContractVersion, true, data, null);

    /// <summary>
    /// Creates an unsuccessful response with a safe fixed message.
    /// </summary>
    /// <param name="code">The stable administration error code used to select a safe message.</param>
    /// <returns>An unsuccessful response containing the error code and message, with no data payload.</returns>
    public static AdministrationResponse<TData> Failure(string code) => new(CurrentContractVersion, false, null, new(code, code switch
    {
        "INVALID_CONTEXT" => "Supply an exact git:, chat: or local: context with 1-255 visible ASCII suffix characters.",
        "INVALID_ARGUMENT" => "Arguments do not match the tool contract.",
        "CONTEXT_UNBOUND" => "The exact context has no active binding.",
        "PROJECT_NOT_FOUND" => "The selected project does not exist.",
        "PROJECT_ARCHIVED" => "The selected project is archived.",
        "ALREADY_EXISTS" => "The selected name already exists.",
        "CONFLICT" => "The operation conflicts with the current state.",
        "STORAGE_UNAVAILABLE" => "Storage is unavailable.",
        "PROJECT_BLOCKED" => "Project deletion is pending.",
        "RECOVERY_REQUIRED" => "The project remains blocked. Inspect its deletion state and retry.",
        "INVALID_PATH" => "Supply an exact, valid relative Markdown path.",
        "UNSAFE_PATH" => "The storage entry cannot be accessed safely.",
        "MEMORY_NOT_FOUND" => "The selected memory does not exist.",
        "CONTENT_TOO_LARGE" => "Memory content exceeds the byte limit.",
        "INVALID_ENCODING" => "Memory content must be valid UTF-8 text.",
        "SEARCH_IO" => "Search process communication failed.",
        "SEARCH_PROTOCOL" => "Search returned invalid data.",
        "SEARCH_TIMEOUT" => "Search exceeded its deadline. Narrow the selection.",
        "SEARCH_FAILED" => "Search could not complete.",
        "SEARCH_UNAVAILABLE" => "The search executable is unavailable.",
        "INVALID_PATTERN" => "Invalid search pattern or glob.",
        "SCAN_LIMIT" => "Scan limit reached. Select a smaller folder and retry.",
        _ => "The operation could not be completed."
    }));

    #endregion

    #region Private Methods

    #endregion
}
