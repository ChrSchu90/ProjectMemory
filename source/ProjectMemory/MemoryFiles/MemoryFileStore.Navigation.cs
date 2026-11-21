namespace ProjectMemory.MemoryFiles;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using ProjectMemory.Tools;
using ProjectMemory.Search;

internal sealed partial class MemoryFileStore
{
    #region Static Fields

    /// <summary>Maximum directory entries examined per request, including ignored names.</summary>
    internal const int MaximumEntries = 4096;
    private static readonly TimeSpan ScanDuration = TimeSpan.FromSeconds(5);

    #endregion

    #region Private Fields

    #endregion

    #region Constructors

    #endregion

    #region Properties

    #endregion

    #region Public Methods

    /// <summary>
    /// Lists reachable Markdown paths in ordinal order within an optional subtree.
    /// </summary>
    /// <param name="id">The active project under its access lock.</param>
    /// <param name="folder">Exact relative directory or empty for the project root.</param>
    /// <param name="offset">Number of paths to skip.</param>
    /// <param name="limit">Maximum paths to return.</param>
    /// <param name="token">Cancellation while scanning.</param>
    /// <returns>The bounded page and whether more paths were observed.</returns>
    internal MemoryListResult List(Guid id, string folder, int offset, int limit, CancellationToken token)
    {
        var entries = Scan(id, folder, offset, limit, Stopwatch.GetTimestamp(), token);
        var page = entries.Skip(offset).Take(limit).Select(entry => entry.Path).ToArray();
        return new MemoryListResult(page, offset, limit, entries.Count - offset > limit);
    }

    /// <summary>Resolves an exact selection using the same managed boundary as listing and reading.</summary>
    /// <param name="id">Revalidated project UUID.</param>
    /// <param name="request">Search selection.</param>
    /// <param name="token">Search cancellation.</param>
    /// <returns>The working directory and allowed paths relative to it.</returns>
    internal (string Directory, string[] Paths) SelectSearchFiles(Guid id, SearchRequest request, CancellationToken token)
    {
        if (request.File is { } file)
        {
            var full = MemoryPath(id, file, false);
            RequireFile(full);
            return (ProjectFolder(id, "", false), [file]);
        }

        var entries = Scan(id, request.Folder, 0, 1, Stopwatch.GetTimestamp(), token, request.Files.MaxDepth);
        // For a lazy project the empty search compiles its pattern on empty stdin.
        if (entries.Count == 0)
            return (AppContext.BaseDirectory, []);
        var directory = ProjectFolder(id, request.Folder, false);
        return (directory, entries.Select(entry => Path.GetRelativePath(directory, entry.File)).ToArray());
    }

    /// <summary>
    /// Enforces the byte budget and checks text eligibility before handing a file to ripgrep.
    /// </summary>
    /// <param name="path">A path within the managed selection.</param>
    /// <param name="consumed">Aggregate size of files inspected.</param>
    /// <returns>Whether the selected file contains eligible UTF-8 text.</returns>
    internal bool EligibleForSearch(string path, ref long consumed)
    {
        checkpoint?.Invoke(LifecycleCheckpoint.SearchRead);
        using var stream = OpenRead(path);
        if (stream.Length > MaximumBytes)
            return false;
        consumed += stream.Length;
        if (consumed > 16L * MaximumBytes)
            throw new MemoryFileException("SCAN_LIMIT");
        var bytes = ReadBytes(stream);
        try
        {
            if (bytes.AsSpan().Contains((byte)0))
                return false;
            _ = Utf8.GetCharCount(bytes);
            return true;
        }
        catch (System.Text.DecoderFallbackException)
        {
            return false;
        }
    }

    #endregion

    #region Private Methods

    private List<(string Path, string File)> Scan(Guid id, string folder, int offset, int limit, long started, CancellationToken token, int maxDepth = 16)
    {
        if (offset < 0 || limit is < 1 or > 200)
            throw new MemoryFileException("INVALID_ARGUMENT");
        if (folder.Length > 0 && !ValidRelativePath(folder, 15))
            throw new MemoryFileException("INVALID_PATH");
        CheckBudget(started, token);
        string scope;
        try
        {
            scope = ProjectFolder(id, folder, create: false);
        }
        catch (MemoryFileException ex) when (ex.Code == "MEMORY_NOT_FOUND" && folder.Length == 0)
        {
            // An active registry project acquires its directory on the first write.
            return [];
        }

        var entries = new List<(string Path, string File)>();
        var visited = 0;
        Visit(scope, folder, 1);
        entries.Sort((a, b) => StringComparer.Ordinal.Compare(a.Path, b.Path));
        CheckBudget(started, token);
        return entries;

        void Visit(string directory, string prefix, int depth)
        {
            foreach (var file in Directory.EnumerateFileSystemEntries(directory))
            {
                CheckBudget(started, token);
                if (++visited > MaximumEntries)
                    throw new MemoryFileException("SCAN_LIMIT");
                var name = Path.GetFileName(file);
                var path = prefix.Length == 0 ? name : prefix + "/" + name;
                if (!ValidRelativePath(path, 16))
                    continue;
                var attributes = Attributes(file) ?? throw new MemoryFileException("MEMORY_NOT_FOUND");
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    if (depth < maxDepth && ValidRelativePath(path, 15))
                    {
                        Visit(file, path, depth + 1);
                    }
                }
                else if (path.EndsWith(".md", StringComparison.Ordinal))
                {
                    checkpoint?.Invoke(LifecycleCheckpoint.ScanEntry);
                    entries.Add((path, file));
                }
            }
        }
    }

    private static void CheckBudget(long started, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Stopwatch.GetElapsedTime(started) >= ScanDuration)
            throw new MemoryFileException("SCAN_LIMIT");
    }

    #endregion
}
