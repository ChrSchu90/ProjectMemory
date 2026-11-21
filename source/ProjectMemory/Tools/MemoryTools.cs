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
    /// Lists exact Markdown paths recursively within the selected project folder.
    /// </summary>
    /// <param name="context_id">The exact bound context ID.</param>
    /// <param name="folder">Exact relative folder, or empty for the root.</param>
    /// <param name="offset">Number of sorted paths to skip.</param>
    /// <param name="limit">Maximum number of paths to return.</param>
    /// <param name="cancellationToken">Cancellation while routing and scanning.</param>
    /// <returns>The versioned page or safe error with matching JSON fallback.</returns>
    [McpServerTool(Name = "list_memories", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(AdministrationResponse<MemoryListResult>))]
    [Description("Recursively list exact Markdown paths in ordinal order. Check has_more for another page. Narrow the folder on SCAN_LIMIT.")]
    public Task<CallToolResult> ListMemoriesAsync(
        [Description("The exact bound git:, chat: or local: context ID."), RegularExpression("^(git|chat|local):[!-~]{1,255}$")] string context_id,
        [Description("Exact project-relative subtree, or empty for the project root."), MaxLength(1024)] string folder = "",
        [Description("Number of paths to skip."), Range(0, int.MaxValue)] int offset = 0,
        [Description("Page size from 1 through 200."), Range(1, 200)] int limit = 50,
        CancellationToken cancellationToken = default) =>
        AdministrationTools.ExecuteAsync(() => manager.RunAsync(context_id, ProjectAccess.Read, id => files.List(id, folder, offset, limit, cancellationToken), cancellationToken), logger);

    /// <summary>
    /// Returns the first literal content match in each matching Markdown file.
    /// </summary>
    /// <param name="context_id">The exact bound context ID.</param>
    /// <param name="query">A single-line nonblank literal, up to 128 UTF-16 code units.</param>
    /// <param name="folder">Exact relative folder, or empty for the root.</param>
    /// <param name="offset">Number of matching files to skip.</param>
    /// <param name="limit">Maximum number of matching files to return.</param>
    /// <param name="cancellationToken">Cancellation while routing, scanning and searching.</param>
    /// <returns>The versioned page of hits or safe error with matching JSON fallback.</returns>
    [McpServerTool(Name = "search_memories", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(AdministrationResponse<MemorySearchResult>))]
    [Description("Search live Markdown for a case-insensitive literal. Return the first hit per file in ordinal path order. Check has_more and skipped_files (oversized or invalid UTF-8). Narrow the folder on SCAN_LIMIT.")]
    public Task<CallToolResult> SearchMemoriesAsync(
        [Description("The exact bound git:, chat: or local: context ID."), RegularExpression("^(git|chat|local):[!-~]{1,255}$")] string context_id,
        [Description("Nonblank literal without control characters, up to 128 UTF-16 code units."), MinLength(1), MaxLength(MemoryFileStore.MaximumQueryLength)] string query,
        [Description("Exact project-relative subtree, or empty for the project root."), MaxLength(1024)] string folder = "",
        [Description("Number of matching files to skip."), Range(0, int.MaxValue)] int offset = 0,
        [Description("Page size from 1 through 200."), Range(1, 200)] int limit = 50,
        CancellationToken cancellationToken = default) =>
        AdministrationTools.ExecuteAsync(() => manager.RunAsync(context_id, ProjectAccess.Read, id => files.Search(id, query, folder, offset, limit, cancellationToken), cancellationToken), logger);

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
        return AdministrationTools.ExecuteAsync(() => manager.RunAsync(context_id, ProjectAccess.Write, id => files.Write(id, path, content, null, cancellationToken), cancellationToken), logger);
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
        return AdministrationTools.ExecuteAsync(() => manager.RunAsync(context_id, ProjectAccess.Read, id => files.Read(id, path, start_line, line_count), cancellationToken), logger);
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
        return AdministrationTools.ExecuteAsync(() => manager.RunAsync(context_id, ProjectAccess.Write, id => files.Write(id, path, content, expected_content_hash, cancellationToken), cancellationToken), logger);
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
        return AdministrationTools.ExecuteAsync(() => manager.RunAsync(context_id, ProjectAccess.Write, id => files.Delete(id, path, expected_content_hash, cancellationToken), cancellationToken), logger);
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

/// <summary>A live page of project-relative Markdown addresses.</summary>
/// <param name="Paths">Exact file paths in ordinal order.</param>
/// <param name="Offset">Number of addresses skipped.</param>
/// <param name="Limit">Requested page capacity.</param>
/// <param name="HasMore">Whether additional paths follow.</param>
internal sealed record MemoryListResult(string[] Paths, int Offset, int Limit, bool HasMore);

/// <summary>A live page of literal Markdown content matches.</summary>
/// <param name="Matches">At most one hit per matching file, in ordinal path order.</param>
/// <param name="Offset">Number of matching files skipped.</param>
/// <param name="Limit">Requested page capacity.</param>
/// <param name="HasMore">Whether additional searchable matches follow.</param>
/// <param name="SkippedFiles">Number of oversized or invalid UTF-8 files in the subtree.</param>
internal sealed record MemorySearchResult(MemorySearchHit[] Matches, int Offset, int Limit, bool HasMore, int SkippedFiles);

/// <summary>The first matching line in one file.</summary>
/// <param name="Path">Exact address for a subsequent read.</param>
/// <param name="Line">One-based LF-delimited line number.</param>
/// <param name="Excerpt">Bounded original text around the first match.</param>
/// <param name="ExcerptTruncated">Whether the excerpt omits part of its source line.</param>
internal sealed record MemorySearchHit(string Path, int Line, string Excerpt, bool ExcerptTruncated);
