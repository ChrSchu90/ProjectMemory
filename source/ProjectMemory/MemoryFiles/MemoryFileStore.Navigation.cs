namespace ProjectMemory.MemoryFiles;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using ProjectMemory.Tools;

internal sealed partial class MemoryFileStore
{
    #region Static Fields

    /// <summary>Maximum directory entries examined per request, including ignored names.</summary>
    internal const int MaximumEntries = 4096;
    /// <summary>Maximum raw file bytes read across a search.</summary>
    internal const int MaximumSearchBytes = 16 * MaximumBytes;
    /// <summary>Maximum single-line query length in UTF-16 code units.</summary>
    internal const int MaximumQueryLength = 128;
    /// <summary>Maximum excerpt length in UTF-16 code units.</summary>
    internal const int MaximumExcerptLength = 192;
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

    /// <summary>
    /// Searches live UTF-8 files for an ordinal case-insensitive literal and returns one hit per file.
    /// </summary>
    /// <param name="id">The active project under its access lock.</param>
    /// <param name="query">A nonblank single-line literal.</param>
    /// <param name="folder">Exact relative directory or empty for the project root.</param>
    /// <param name="offset">Number of matching files to skip.</param>
    /// <param name="limit">Maximum matching files to return.</param>
    /// <param name="token">Cancellation while enumerating and between bounded file reads.</param>
    /// <returns>The page of hits and the count of oversized or invalid UTF-8 files omitted.</returns>
    internal MemorySearchResult Search(Guid id, string query, string folder, int offset, int limit, CancellationToken token)
    {
        if (query.Length > MaximumQueryLength || string.IsNullOrWhiteSpace(query) || query.Any(char.IsControl))
            throw new MemoryFileException("INVALID_ARGUMENT");
        var started = Stopwatch.GetTimestamp();
        var entries = Scan(id, folder, offset, limit, started, token);
        var matches = new List<MemorySearchHit>(limit);
        var matchCount = 0;
        var skippedFiles = 0;
        long bytesRead = 0;
        foreach (var entry in entries)
        {
            CheckBudget(started, token);
            checkpoint?.Invoke(LifecycleCheckpoint.SearchRead);
            CheckBudget(started, token);
            using var stream = OpenRead(entry.File);
            var length = stream.Length;
            if (length > MaximumBytes)
            {
                skippedFiles++;
                continue;
            }

            bytesRead += length;
            if (bytesRead > MaximumSearchBytes)
                throw new MemoryFileException("SCAN_LIMIT");
            var bytes = ReadBytes(stream);
            string content;
            try
            {
                content = Decode(bytes);
            }
            catch (MemoryFileException ex) when (ex.Code == "INVALID_ENCODING")
            {
                skippedFiles++;
                continue;
            }

            var index = content.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                continue;
            matchCount++;
            if (matchCount > offset && matches.Count < limit)
            {
                matches.Add(Hit(entry.Path, content, index));
            }
        }

        CheckBudget(started, token);
        return new MemorySearchResult(matches.ToArray(), offset, limit, matchCount - offset > limit, skippedFiles);
    }

    #endregion

    #region Private Methods

    private List<(string Path, string File)> Scan(Guid id, string folder, int offset, int limit, long started, CancellationToken token)
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
        Visit(scope, folder);
        entries.Sort((a, b) => StringComparer.Ordinal.Compare(a.Path, b.Path));
        CheckBudget(started, token);
        return entries;

        void Visit(string directory, string prefix)
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
                    if (ValidRelativePath(path, 15))
                    {
                        Visit(file, path);
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

    private static MemorySearchHit Hit(string path, string content, int index)
    {
        var preceding = content.AsSpan(0, index);
        var line = 1 + preceding.Count('\n');
        var lineStart = preceding.LastIndexOf('\n') + 1;
        var newline = content.IndexOf('\n', index);
        var lineEnd = newline < 0 ? content.Length : newline;
        if (lineEnd > index && content[lineEnd - 1] == '\r')
        {
            lineEnd--;
        }

        var start = Math.Max(lineStart, Math.Min(index - 32, lineEnd - MaximumExcerptLength));
        if (char.IsLowSurrogate(content[start]))
        {
            start++;
        }

        var end = Math.Min(lineEnd, start + MaximumExcerptLength);
        if (end < lineEnd && char.IsHighSurrogate(content[end - 1]))
        {
            end--;
        }

        return new MemorySearchHit(path, line, content[start..end], start > lineStart || end < lineEnd);
    }

    #endregion
}
