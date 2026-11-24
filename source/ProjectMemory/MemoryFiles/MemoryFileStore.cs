namespace ProjectMemory.MemoryFiles;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Options;
using ProjectMemory.Configuration;
using ProjectMemory.Database;
using ProjectMemory.Search;
using ProjectMemory.Tools;

/// <summary>
/// Reads and publishes bounded ordinary Markdown files below an immutable project UUID.
/// </summary>
/// <param name="options">The configured private data root.</param>
/// <param name="checkpoint">Optional observer used to exercise interruption windows.</param>
internal sealed class MemoryFileStore(IOptions<ProjectMemoryOptions> options, Action<LifecycleCheckpoint>? checkpoint = null)
{
    #region Static Fields

    /// <summary>
    /// Maximum size of a complete Markdown file in bytes, including for line-range reads.
    /// </summary>
    internal const int MaximumBytes = 1_048_576;

    /// <summary>Maximum directory entries examined per request, including ignored names.</summary>
    internal const int MaximumEntries = 4096;

    private const int MaximumTreeDepth = 128;
    private const int MaximumPreflightEntries = 100_000;
    private static readonly TimeSpan PreflightDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ScanDuration = TimeSpan.FromSeconds(5);
    private static readonly UTF8Encoding Utf8 = new(false, true);

    #endregion

    #region Private Fields

    #endregion

    #region Constructors

    #endregion

    #region Properties

    #endregion

    #region Public Methods

    /// <summary>
    /// Validates the exact path, including its UTF-8 component lengths.
    /// </summary>
    /// <param name="path">The unmodified, slash-separated Markdown address.</param>
    /// <returns>Whether the path is addressable through an ordinary memory tool.</returns>
    internal static bool ValidPath(string path)
        => path.EndsWith(".md", StringComparison.Ordinal) && ValidRelativePath(path, 16);

    /// <summary>
    /// Reads full UTF-8 text or a bounded line range and hashes all raw bytes.
    /// </summary>
    /// <param name="id">The revalidated active project ID.</param>
    /// <param name="path">The validated memory address.</param>
    /// <param name="startLine">The first requested line, counting from one.</param>
    /// <param name="lineCount">The maximum number of lines to return.</param>
    /// <returns>The selected text, address and whole-file hash.</returns>
    internal MemoryReadResult Read(Guid id, string path, int startLine, int lineCount)
    {
        var file = MemoryPath(id, path, create: false);
        RequireFile(file);
        using var stream = OpenRead(file);
        checkpoint?.Invoke(LifecycleCheckpoint.Reading);
        var bytes = ReadBytes(stream);
        var text = Decode(bytes);

        var start = SkipLines(text, 0, startLine - 1);
        var end = SkipLines(text, start, lineCount);
        return new MemoryReadResult(path, Hash(bytes), text[start..end]);
    }

    /// <summary>
    /// Stages complete UTF-8 bytes then creates or conditionally replaces one file.
    /// </summary>
    /// <param name="id">The revalidated active project.</param>
    /// <param name="path">The validated memory address.</param>
    /// <param name="content">The complete replacement text.</param>
    /// <param name="expected">The expected raw hash, or null for exclusive creation.</param>
    /// <param name="token">Cancellation before publication.</param>
    /// <returns>The exact path and published raw hash.</returns>
    internal MemoryChangeResult Write(Guid id, string path, string content, string? expected, CancellationToken token)
    {
        if (content.Length > MaximumBytes || Utf8.GetByteCount(content) > MaximumBytes)
            throw new MemoryFileException("CONTENT_TOO_LARGE");
        token.ThrowIfCancellationRequested();
        var bytes = Utf8.GetBytes(content);
        var file = MemoryPath(id, path, create: expected is null);
        if (expected is null)
        {
            if (FilePresent(file))
                throw new MemoryFileException("ALREADY_EXISTS");
        }
        else
        {
            RequireHash(file, expected);
        }

        var result = new MemoryChangeResult(path, Hash(bytes));
        var temporary = Path.Combine(Path.GetDirectoryName(file)!, ".pm-" + Guid.NewGuid().ToString("N"));
        var settings = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            settings.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        // Only a successful CreateNew grants ownership of the temporary name.
        var stream = new FileStream(temporary, settings);
        var published = false;
        try
        {
            using (stream)
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            checkpoint?.Invoke(LifecycleCheckpoint.Staged);
            token.ThrowIfCancellationRequested();
            // The exclusive project lease protects the create check and hash CAS.
            // Windows needs replacement semantics for existing files held by delete-sharing readers.
            // Both paths share a parent; Linux retains the existing rename publication.
            if (OperatingSystem.IsWindows() && expected is not null)
            {
                File.Replace(temporary, file, null);
            }
            else
            {
                File.Move(temporary, file, overwrite: true);
            }
            published = true;
            checkpoint?.Invoke(LifecycleCheckpoint.Published);
            return result;
        }
        finally
        {
            if (!published)
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>
    /// Removes only the selected regular file after verifying its entire current hash.
    /// </summary>
    /// <param name="id">The revalidated active project.</param>
    /// <param name="path">The exact validated address.</param>
    /// <param name="expected">The caller's whole-file hash.</param>
    /// <param name="token">Cancellation before unlink.</param>
    /// <returns>The address and the verified hash of the deleted bytes.</returns>
    internal MemoryChangeResult Delete(Guid id, string path, string expected, CancellationToken token)
    {
        var file = MemoryPath(id, path, create: false);
        RequireHash(file, expected);
        token.ThrowIfCancellationRequested();
        File.Delete(file);
        return new MemoryChangeResult(path, expected.ToLowerInvariant());
    }

    /// <summary>
    /// Quarantines and purges the tree belonging to a durable deletion intent.
    /// </summary>
    /// <param name="id">The immutable ID from the blocked registry row.</param>
    /// <param name="token">Cancellation between recoverable steps.</param>
    internal void Purge(Guid id, CancellationToken token)
    {
        var root = DataRoot();
        var projects = DirectoryAt(root, "projects", create: true);
        var quarantine = DirectoryAt(root, "quarantine", create: true);
        var name = id.ToString("D");
        var source = Path.Combine(projects, name);
        var target = Path.Combine(quarantine, name);
        MoveToQuarantine(source, target);
        checkpoint?.Invoke(LifecycleCheckpoint.Quarantined);
        token.ThrowIfCancellationRequested();
        if (Attributes(target) is not null)
        {
            PurgeChildren(target, token, 0);
            Directory.Delete(target);
        }

        if (Attributes(source) is not null || Attributes(target) is not null)
            throw new MemoryFileException("UNSAFE_PATH");
        checkpoint?.Invoke(LifecycleCheckpoint.Purged);
    }

    /// <summary>
    /// Moves one blocked project between its validated parents. Directory.Move requires a same-filesystem rename.
    /// </summary>
    /// <param name="source">The server-derived project UUID path.</param>
    /// <param name="target">The same UUID in the private quarantine.</param>
    internal void MoveToQuarantine(string source, string target)
    {
        var original = ExactAttributes(source);
        var quarantined = ExactAttributes(target);
        if (original?.HasFlag(FileAttributes.Directory) == false || quarantined?.HasFlag(FileAttributes.Directory) == false
            || (original is not null && quarantined is not null))
            throw new MemoryFileException("UNSAFE_PATH");
        if (quarantined is not null)
        {
            // A restored quarantine can be on a different volume. Prove the current
            // rename boundary before purging. Both names remain blocked by the intent.
            Directory.Move(target, source);
            checkpoint?.Invoke(LifecycleCheckpoint.ReplayMoved);
        }
        else if (original is null)
            return;

        Directory.Move(source, target);
    }

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
        if (offset < 0 || limit is < 1 or > 200)
            throw new MemoryFileException("INVALID_ARGUMENT");

        var entries = Scan(id, folder, token);
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

        var entries = Scan(id, request.Folder, token, request.Files.MaxDepth);
        // For a lazy project the empty search compiles its pattern on empty stdin.
        if (entries.Count == 0)
            return (AppContext.BaseDirectory, []);
        var directory = ProjectFolder(id, request.Folder, false);
        // MCP selectors always use '/', regardless of the native filesystem separator.
        return (directory, entries.Select(entry => Path.GetRelativePath(directory, entry.File).Replace(Path.DirectorySeparatorChar, '/')).ToArray());
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

    /// <summary>
    /// Inspects the managed roots and SQLite names before migration or destructive recovery.
    /// Ordinary offline files, including unaddressable names and staging remnants, are left in place.
    /// </summary>
    /// <param name="database">The actual lifecycle and migration paths used by the host.</param>
    /// <param name="token">Cancellation between directory entries.</param>
    /// <returns>The canonical UUID trees to reconcile with SQLite before replay.</returns>
    internal (HashSet<Guid> Projects, HashSet<Guid> Quarantine) Preflight(DatabasePaths database, CancellationToken token)
    {
        var started = Stopwatch.GetTimestamp();
        var root = DataRoot();
        foreach (var file in new[] { database.DatabasePath, database.MigrationPath, database.BackupPath })
        {
            foreach (var suffix in new[] { "", "-shm", "-wal", "-journal" })
            {
                if (Attributes(file + suffix)?.HasFlag(FileAttributes.Directory) == true)
                    throw new MemoryFileException("UNSAFE_PATH");
            }
        }

        if (Attributes(database.MigrationLockPath)?.HasFlag(FileAttributes.Directory) == true)
            throw new MemoryFileException("UNSAFE_PATH");

        var visited = 0;
        var projects = InspectRoot("projects");
        var quarantine = InspectRoot("quarantine");
        if (projects.Overlaps(quarantine))
            throw new MemoryFileException("UNSAFE_PATH");
        CheckBudget();
        return (projects, quarantine);

        HashSet<Guid> InspectRoot(string name)
        {
            var ids = new HashSet<Guid>();
            var parent = Path.Combine(root, name);
            var attributes = Attributes(parent);
            if (attributes is null)
                return ids;
            if (!attributes.Value.HasFlag(FileAttributes.Directory))
                throw new MemoryFileException("UNSAFE_PATH");

            foreach (var path in Directory.EnumerateFileSystemEntries(parent))
            {
                var entry = Inspect(path);
                var component = Path.GetFileName(path);
                if (!entry.HasFlag(FileAttributes.Directory) || !Guid.TryParseExact(component, "D", out var id)
                    || component != id.ToString("D") || id == Guid.Empty)
                    throw new MemoryFileException("UNSAFE_PATH");
                ids.Add(id);
                Visit(path, 0);
            }

            return ids;
        }

        void Visit(string directory, int depth)
        {
            if (depth > MaximumTreeDepth)
                throw new MemoryFileException("UNSAFE_PATH");
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                if (Inspect(path).HasFlag(FileAttributes.Directory))
                {
                    Visit(path, depth + 1);
                }
            }
        }

        FileAttributes Inspect(string path)
        {
            visited++;
            CheckBudget();
            return Attributes(path) ?? throw new MemoryFileException("UNSAFE_PATH");
        }

        void CheckBudget()
        {
            token.ThrowIfCancellationRequested();
            if (visited > MaximumPreflightEntries || Stopwatch.GetElapsedTime(started) >= PreflightDuration)
                throw new MemoryFileException("SCAN_LIMIT");
        }
    }

    #endregion

    #region Private Methods

    private static bool ValidRelativePath(string path, int maximumComponents)
    {
        if (path.Length is 0 or > 1024)
            return false;
        var parts = path.Split('/');
        if (OperatingSystem.IsWindows() && parts.Any(IsReservedWindowsDeviceName))
            return false;
        return parts.Length <= maximumComponents && parts.All(part => part.Length > 0
            && part[0] is not ('.' or ' ') && part[^1] is not ('.' or ' ')
            && part.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ' ') && Utf8.GetByteCount(part) <= 255)
            && Utf8.GetByteCount(path) <= 1024;
    }

    private static bool IsReservedWindowsDeviceName(string part)
    {
        // Win32 device names are reserved for files and directories, even when followed by an extension.
        var dot = part.IndexOf('.');
        var name = part.AsSpan(0, dot >= 0 ? dot : part.Length).TrimEnd(' ');
        return name.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || name.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || name.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || (name.Length == 4 && (name[..3].Equals("COM", StringComparison.OrdinalIgnoreCase)
                || name[..3].Equals("LPT", StringComparison.OrdinalIgnoreCase))
                && "123456789¹²³".Contains(name[3]));
    }

    private void PurgeChildren(string parent, CancellationToken token, int depth)
    {
        if (depth > MaximumTreeDepth)
            throw new MemoryFileException("UNSAFE_PATH");
        foreach (var path in Directory.EnumerateFileSystemEntries(parent))
        {
            token.ThrowIfCancellationRequested();
            var attributes = Attributes(path) ?? throw new MemoryFileException("CONFLICT");
            if (attributes.HasFlag(FileAttributes.Directory))
            {
                PurgeChildren(path, token, depth + 1);
                Directory.Delete(path);
            }
            else
            {
                File.Delete(path);
            }

            checkpoint?.Invoke(LifecycleCheckpoint.PurgeEntry);
        }
    }

    private string DataRoot()
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Value.DataDirectory));
        if (Attributes(root) is not { } attributes || !attributes.HasFlag(FileAttributes.Directory))
            throw new MemoryFileException("STORAGE_UNAVAILABLE");
        return root;
    }

    private string MemoryPath(Guid id, string path, bool create)
    {
        if (!ValidPath(path))
            throw new MemoryFileException("INVALID_PATH");
        var slash = path.LastIndexOf('/');
        var parent = ProjectFolder(id, slash < 0 ? "" : path[..slash], create);
        return Path.Combine(parent, Leaf(path));
    }

    private string ProjectFolder(Guid id, string folder, bool create)
    {
        var projects = DirectoryAt(DataRoot(), "projects", create);
        var current = DirectoryAt(projects, id.ToString("D"), create);
        if (folder.Length > 0)
        {
            foreach (var part in folder.Split('/'))
            {
                current = DirectoryAt(current, part, create);
            }
        }

        return current;
    }

    private static string DirectoryAt(string parent, string name, bool create)
    {
        var path = Path.Combine(parent, name);
        if (ExactAttributes(path) is { } existing)
        {
            if (!existing.HasFlag(FileAttributes.Directory))
                throw new MemoryFileException("UNSAFE_PATH");
        }
        else if (!create)
            throw new MemoryFileException("MEMORY_NOT_FOUND");
        else if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private static FileAttributes? Attributes(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new MemoryFileException("UNSAFE_PATH");
        return attributes;
    }

    private static FileAttributes? ExactAttributes(string path)
    {
        var attributes = Attributes(path);
        if (attributes is not null)
        {
            // Even on case-folding volumes only the actual ordinal spelling is addressable.
            var parent = Path.GetDirectoryName(path)!;
            if (!Directory.EnumerateFileSystemEntries(parent, Path.GetFileName(path)).Contains(path, StringComparer.Ordinal))
                throw new MemoryFileException("UNSAFE_PATH");
        }

        return attributes;
    }

    private static bool FilePresent(string file)
    {
        var attributes = ExactAttributes(file);
        if (attributes?.HasFlag(FileAttributes.Directory) == true)
            throw new MemoryFileException("UNSAFE_PATH");
        return attributes is not null;
    }

    private static void RequireFile(string file)
    {
        if (!FilePresent(file))
            throw new MemoryFileException("MEMORY_NOT_FOUND");
    }

    private static FileStream OpenRead(string file) =>
        new(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

    private static byte[] ReadBytes(FileStream stream)
    {
        var length = stream.Length;
        if (length > MaximumBytes)
            throw new MemoryFileException("CONTENT_TOO_LARGE");
        var bytes = new byte[(int)length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static void RequireHash(string file, string expected)
    {
        RequireFile(file);
        using var stream = OpenRead(file);
        if (!string.Equals(Hash(ReadBytes(stream)), expected, StringComparison.OrdinalIgnoreCase))
            throw new MemoryFileException("CONFLICT");
    }

    private static string Decode(byte[] bytes)
    {
        try
        {
            return Utf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new MemoryFileException("INVALID_ENCODING");
        }
    }

    private static string Leaf(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static int SkipLines(string text, int index, int count)
    {
        while (count-- > 0 && index < text.Length)
        {
            var next = text.IndexOf('\n', index);
            index = next < 0 ? text.Length : next + 1;
        }

        return index;
    }

    private List<(string Path, string File)> Scan(Guid id, string folder, CancellationToken token, int maxDepth = 16)
    {
        var started = Stopwatch.GetTimestamp();
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

/// <summary>
/// Identifies observable boundaries in the write and deletion protocols for fault tests.
/// </summary>
internal enum LifecycleCheckpoint
{
    /// <summary>The first context lookup has completed.</summary>
    Routed,
    /// <summary>A read has entered the authorized file operation.</summary>
    Reading,
    /// <summary>The temporary file is complete and flushed.</summary>
    Staged,
    /// <summary>The complete target is visible, before the result is returned.</summary>
    Published,
    /// <summary>The deletion intent and archive block have committed.</summary>
    IntentCommitted,
    /// <summary>The surviving quarantined tree was moved back under its blocked UUID during replay.</summary>
    ReplayMoved,
    /// <summary>The quarantine move has completed or the tree was already absent.</summary>
    Quarantined,
    /// <summary>A quarantine child was removed.</summary>
    PurgeEntry,
    /// <summary>Both tree names are absent, before registry cleanup.</summary>
    Purged,
    /// <summary>An addressable navigation candidate has been inspected.</summary>
    ScanEntry,
    /// <summary>Search is about to reopen an enumerated file for its bounded content read.</summary>
    SearchRead
}
