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
    public static AdministrationResponse<TData> Success(TData data) => new("1.0", true, data, null);

    /// <summary>
    /// Creates an unsuccessful response with a safe fixed message.
    /// </summary>
    /// <param name="code">The stable administration error code used to select a safe message.</param>
    /// <returns>An unsuccessful response containing the error code and message, with no data payload.</returns>
    public static AdministrationResponse<TData> Failure(string code) => new("1.0", false, null, new(code, code switch
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
        _ => "The operation could not be completed."
    }));

    #endregion

    #region Private Methods

    #endregion
}
