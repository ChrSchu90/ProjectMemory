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
        using var parent = OpenParent(id, path, false);
        var (bytes, _) = parent.Read(Leaf(path), MaximumBytes);
        VerifyParent(parent, id, path);
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
        var bytes = Utf8.GetBytes(content);
        var name = Leaf(path);
        using var parent = OpenParent(id, path, expected is null);
        if (expected is not null)
        {
            RequireHash(parent, name, expected);
        }
        else if (parent.Lookup(name) is { } existing)
        {
            throw new MemoryFileException(existing.IsRegular && existing.Links == 1 ? "ALREADY_EXISTS" : "UNSAFE_PATH");
        }

        var temporary = ".pm-" + Guid.NewGuid().ToString("N");
        using var handle = parent.CreateTemporary(temporary);
        var published = false;
        try
        {
            checkpoint?.Invoke(LifecycleCheckpoint.StagingOpened);
            RandomAccess.Write(handle, bytes, 0);
            RandomAccess.FlushToDisk(handle);
            checkpoint?.Invoke(LifecycleCheckpoint.Staged);
            var original = expected is null ? null : RequireHash(parent, name, expected);
            VerifyParent(parent, id, path);
            if (!parent.OwnsTemporary(temporary, handle) || (original is not null && parent.Lookup(name) != original))
                throw new MemoryFileException("CONFLICT");
            token.ThrowIfCancellationRequested();
            parent.Rename(temporary, parent, name, noReplace: expected is null);
            published = true;
            parent.Sync();
            return new MemoryChangeResult(path, Hash(bytes));
        }
        finally
        {
            if (!published && parent.OwnsTemporary(temporary, handle))
            {
                parent.Remove(temporary);
                parent.Sync();
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
        using var parent = OpenParent(id, path, false);
        var name = Leaf(path);
        var original = RequireHash(parent, name, expected);
        VerifyParent(parent, id, path);
        if (parent.Lookup(name) != original)
            throw new MemoryFileException("CONFLICT");
        token.ThrowIfCancellationRequested();
        parent.Remove(name);
        parent.Sync();
        return new MemoryChangeResult(path, expected.ToLowerInvariant());
    }

    /// <summary>
    /// Quarantines and purges the tree belonging to a durable deletion intent.
    /// </summary>
    /// <param name="id">The immutable ID from the blocked registry row.</param>
    /// <param name="token">Cancellation between recoverable steps.</param>
    internal void Purge(Guid id, CancellationToken token)
    {
        using var root = LinuxDirectory.OpenRoot(options.Value.DataDirectory);
        using var projects = root.DirectoryAt("projects", true);
        using var quarantine = root.DirectoryAt("quarantine", true);
        var name = id.ToString("D");
        var source = projects.Lookup(name);
        var target = quarantine.Lookup(name);
        if (source is not null)
        {
            if (!source.IsDirectory || target is not null)
                throw new MemoryFileException("UNSAFE_PATH");
            projects.Rename(name, quarantine, name, noReplace: true);
        }

        // Replay must flush both parents even when the previous call already performed the rename.
        quarantine.Sync();
        projects.Sync();
        checkpoint?.Invoke(LifecycleCheckpoint.Quarantined);
        token.ThrowIfCancellationRequested();
        if (quarantine.Lookup(name) is not null)
        {
            using var tree = quarantine.DirectoryAt(name);
            PurgeChildren(tree, token, 0);
            quarantine.Remove(name, directory: true);
        }

        quarantine.Sync();
        if (projects.Lookup(name) is not null || quarantine.Lookup(name) is not null)
            throw new MemoryFileException("UNSAFE_PATH");
        checkpoint?.Invoke(LifecycleCheckpoint.Purged);
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

    private void PurgeChildren(LinuxDirectory parent, CancellationToken token, int depth)
    {
        if (depth > 128)
            throw new MemoryFileException("UNSAFE_PATH");
        foreach (var name in parent.Names())
        {
            token.ThrowIfCancellationRequested();
            var identity = parent.Lookup(name) ?? throw new MemoryFileException("CONFLICT");
            if (identity.IsDirectory)
            {
                using var child = parent.DirectoryAt(name);
                PurgeChildren(child, token, depth + 1);
                parent.Remove(name, directory: true);
            }
            else
            {
                if (!identity.IsRegular || identity.Links != 1)
                    throw new MemoryFileException("UNSAFE_PATH");
                parent.Remove(name);
            }

            checkpoint?.Invoke(LifecycleCheckpoint.PurgeEntry);
        }

        parent.Sync();
    }

    private LinuxDirectory OpenParent(Guid id, string path, bool create)
    {
        if (!ValidPath(path))
            throw new MemoryFileException("INVALID_PATH");
        using var root = LinuxDirectory.OpenRoot(options.Value.DataDirectory);
        var slash = path.LastIndexOf('/');
        return OpenFolder(root, id, slash < 0 ? "" : path[..slash], create);
    }

    private static LinuxDirectory OpenFolder(LinuxDirectory root, Guid id, string folder, bool create)
    {
        using var projects = root.DirectoryAt("projects", create);
        var current = projects.DirectoryAt(id.ToString("D"), create);
        if (folder.Length == 0)
            return current;
        try
        {
            foreach (var part in folder.Split('/'))
            {
                var next = current.DirectoryAt(part, create);
                current.Dispose();
                current = next;
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
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

    private void VerifyParent(LinuxDirectory pinned, Guid id, string path)
    {
        using var current = OpenParent(id, path, false);
        if (!pinned.SameDirectory(current))
            throw new MemoryFileException("CONFLICT");
    }

    private static FileIdentity RequireHash(LinuxDirectory parent, string name, string expected)
    {
        var (bytes, identity) = parent.Read(name, MaximumBytes);
        if (!string.Equals(Hash(bytes), expected, StringComparison.OrdinalIgnoreCase))
            throw new MemoryFileException("CONFLICT");
        return identity;
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
    /// <summary>The private temporary file has been opened.</summary>
    StagingOpened,
    /// <summary>The temporary file is complete and flushed.</summary>
    Staged,
    /// <summary>The deletion intent and archive block have committed.</summary>
    IntentCommitted,
    /// <summary>The quarantine rename and parent directory flushes have completed.</summary>
    Quarantined,
    /// <summary>A quarantine child was removed.</summary>
    PurgeEntry,
    /// <summary>The tree removal has been flushed, before registry cleanup.</summary>
    Purged,
    /// <summary>A navigation candidate has been inspected, before checking its name again.</summary>
    ScanEntry,
    /// <summary>Search is about to reopen an enumerated file for its bounded content read.</summary>
    SearchRead
}
