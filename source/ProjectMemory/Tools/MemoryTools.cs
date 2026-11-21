namespace ProjectMemory.Tools;

using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ProjectMemory.MemoryFiles;
using ProjectMemory.Project;

/// <summary>
/// Exposes exact-path Markdown operations within a revalidated active project.
/// </summary>
/// <param name="manager">The context and project access coordinator.</param>
/// <param name="files">The bounded Markdown store.</param>
/// <param name="logger">The logger for safe storage diagnostics.</param>
[McpServerToolType]
internal sealed class MemoryTools(MemoryManager manager, MemoryFileStore files, ILogger<MemoryTools> logger)
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
    /// Create a Markdown file exclusively at the exact project-relative path.
    /// </summary>
    /// <param name="context_id">The exact bound git:, chat: or local: context ID.</param>
    /// <param name="path">Exact slash-separated relative .md path, up to 16 components and 1024 UTF-8 bytes.</param>
    /// <param name="content">Complete UTF-8 Markdown text, up to 1048576 bytes.</param>
    /// <param name="cancellationToken">Cancellation before access or commit.</param>
    /// <returns>The versioned tool result with matching JSON text fallback.</returns>
    [McpServerTool(Name = "create_memory", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(AdministrationResponse<MemoryChangeResult>))]
    [Description("Create a Markdown file exclusively at the exact project-relative path.")]
    public Task<CallToolResult> CreateMemoryAsync(
        [Description("The exact bound git:, chat: or local: context ID."), RegularExpression("^(git|chat|local):[!-~]{1,255}$")] string context_id,
        [Description("Exact slash-separated relative .md path, up to 16 components and 1024 UTF-8 bytes."), MaxLength(1024)] string path,
        [Description("Complete UTF-8 Markdown text, up to 1048576 bytes."), MaxLength(MemoryFileStore.MaximumBytes)] string content,
        CancellationToken cancellationToken = default)
    {
        return AdministrationTools.ExecuteAsync(() => manager.RunAsync(context_id, id => files.Write(id, path, content, null, cancellationToken), cancellationToken), logger);
    }

    /// <summary>
    /// Read the full file or a line range and return the hash of all original bytes.
    /// </summary>
    /// <param name="context_id">The exact bound git:, chat: or local: context ID.</param>
    /// <param name="path">Exact slash-separated relative .md path, up to 16 components and 1024 UTF-8 bytes.</param>
    /// <param name="start_line">First LF-delimited line to return, starting at one.</param>
    /// <param name="line_count">Maximum lines to return, including their original delimiters.</param>
    /// <param name="cancellationToken">Cancellation before access or commit.</param>
    /// <returns>The versioned tool result with matching JSON text fallback.</returns>
    [McpServerTool(Name = "read_memory", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(AdministrationResponse<MemoryReadResult>))]
    [Description("Read the full file or a line range and return the hash of all original bytes.")]
    public Task<CallToolResult> ReadMemoryAsync(
        [Description("The exact bound git:, chat: or local: context ID."), RegularExpression("^(git|chat|local):[!-~]{1,255}$")] string context_id,
        [Description("Exact slash-separated relative .md path, up to 16 components and 1024 UTF-8 bytes."), MaxLength(1024)] string path,
        [Description("First LF-delimited line to return, starting at one."), Range(1, int.MaxValue)] int start_line = 1,
        [Description("Maximum lines to return, including their original delimiters."), Range(1, MemoryFileStore.MaximumBytes + 1)] int line_count = MemoryFileStore.MaximumBytes + 1,
        CancellationToken cancellationToken = default)
    {
        if (start_line < 1 || line_count is < 1 or > MemoryFileStore.MaximumBytes + 1)
            return AdministrationTools.ExecuteAsync(() => Task.FromResult(AdministrationResponse<MemoryReadResult>.Failure("INVALID_ARGUMENT")), logger);
        return AdministrationTools.ExecuteAsync(() => manager.RunAsync(context_id, id => files.Read(id, path, start_line, line_count), cancellationToken), logger);
    }

    /// <summary>
    /// Replace all content only if the current raw bytes match the expected hash.
    /// </summary>
    /// <param name="context_id">The exact bound git:, chat: or local: context ID.</param>
    /// <param name="path">Exact slash-separated relative .md path, up to 16 components and 1024 UTF-8 bytes.</param>
    /// <param name="content">Complete UTF-8 Markdown text, up to 1048576 bytes.</param>
    /// <param name="expected_content_hash">Whole-file SHA-256 as 64 hexadecimal digits.</param>
    /// <param name="cancellationToken">Cancellation before access or commit.</param>
    /// <returns>The versioned tool result with matching JSON text fallback.</returns>
    [McpServerTool(Name = "update_memory", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(AdministrationResponse<MemoryChangeResult>))]
    [Description("Replace all content only if the current raw bytes match the expected hash.")]
    public Task<CallToolResult> UpdateMemoryAsync(
        [Description("The exact bound git:, chat: or local: context ID."), RegularExpression("^(git|chat|local):[!-~]{1,255}$")] string context_id,
        [Description("Exact slash-separated relative .md path, up to 16 components and 1024 UTF-8 bytes."), MaxLength(1024)] string path,
        [Description("Complete UTF-8 Markdown text, up to 1048576 bytes."), MaxLength(MemoryFileStore.MaximumBytes)] string content,
        [Description("Whole-file SHA-256 as 64 hexadecimal digits."), RegularExpression("^[0-9a-fA-F]{64}$")] string expected_content_hash,
        CancellationToken cancellationToken = default)
    {
        if (!MemoryManager.ValidHash(expected_content_hash))
            return AdministrationTools.ExecuteAsync(() => Task.FromResult(AdministrationResponse<MemoryChangeResult>.Failure("INVALID_ARGUMENT")), logger);
        return AdministrationTools.ExecuteAsync(() => manager.RunAsync(context_id, id => files.Write(id, path, content, expected_content_hash, cancellationToken), cancellationToken), logger);
    }

    /// <summary>
    /// Delete only this Markdown file if its current raw bytes match the expected hash.
    /// </summary>
    /// <param name="context_id">The exact bound git:, chat: or local: context ID.</param>
    /// <param name="path">Exact slash-separated relative .md path, up to 16 components and 1024 UTF-8 bytes.</param>
    /// <param name="expected_content_hash">Whole-file SHA-256 as 64 hexadecimal digits.</param>
    /// <param name="cancellationToken">Cancellation before access or commit.</param>
    /// <returns>The versioned tool result with matching JSON text fallback.</returns>
    [McpServerTool(Name = "delete_memory", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(AdministrationResponse<MemoryChangeResult>))]
    [Description("Delete only this Markdown file if its current raw bytes match the expected hash.")]
    public Task<CallToolResult> DeleteMemoryAsync(
        [Description("The exact bound git:, chat: or local: context ID."), RegularExpression("^(git|chat|local):[!-~]{1,255}$")] string context_id,
        [Description("Exact slash-separated relative .md path, up to 16 components and 1024 UTF-8 bytes."), MaxLength(1024)] string path,
        [Description("Whole-file SHA-256 as 64 hexadecimal digits."), RegularExpression("^[0-9a-fA-F]{64}$")] string expected_content_hash,
        CancellationToken cancellationToken = default)
    {
        if (!MemoryManager.ValidHash(expected_content_hash))
            return AdministrationTools.ExecuteAsync(() => Task.FromResult(AdministrationResponse<MemoryChangeResult>.Failure("INVALID_ARGUMENT")), logger);
        return AdministrationTools.ExecuteAsync(() => manager.RunAsync(context_id, id => files.Delete(id, path, expected_content_hash, cancellationToken), cancellationToken), logger);
    }

    #endregion

    #region Private Methods

    #endregion
}

/// <summary>
/// Describes the published or removed file.
/// </summary>
/// <param name="Path">The exact project-relative path.</param>
/// <param name="ContentHash">SHA-256 of the published or removed bytes.</param>
internal sealed record MemoryChangeResult(string Path, string ContentHash);

/// <summary>
/// Contains a full or targeted read of a Markdown file.
/// </summary>
/// <param name="Path">The exact project-relative path.</param>
/// <param name="ContentHash">SHA-256 of the complete original bytes.</param>
/// <param name="Content">The requested text with original line endings.</param>
internal sealed record MemoryReadResult(string Path, string ContentHash, string Content);
