namespace ProjectMemory.MemoryFiles;

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Options;
using ProjectMemory.Configuration;
using ProjectMemory.Tools;

/// <summary>
/// Reads and publishes bounded ordinary Markdown files below an immutable project UUID.
/// </summary>
/// <param name="options">The configured private data root.</param>
/// <param name="checkpoint">Optional observer used to exercise interruption windows.</param>
internal sealed partial class MemoryFileStore(IOptions<ProjectMemoryOptions> options, Action<LifecycleCheckpoint>? checkpoint = null)
{
    #region Static Fields

    /// <summary>
    /// Maximum size of a complete Markdown file in bytes, including for line-range reads.
    /// </summary>
    internal const int MaximumBytes = 1_048_576;
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
            // Both names share a parent. Use the runtime's rename path for both operations.
            File.Move(temporary, file, overwrite: true);
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

    #endregion

    #region Private Methods

    private static bool ValidRelativePath(string path, int maximumComponents)
    {
        if (path.Length is 0 or > 1024)
            return false;
        var parts = path.Split('/');
        return parts.Length <= maximumComponents && parts.All(part => part.Length > 0
            && part[0] is not ('.' or ' ') && part[^1] is not ('.' or ' ')
            && part.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ' ') && Utf8.GetByteCount(part) <= 255)
            && Utf8.GetByteCount(path) <= 1024;
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
