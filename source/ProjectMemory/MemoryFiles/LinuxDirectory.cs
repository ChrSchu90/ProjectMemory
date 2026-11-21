namespace ProjectMemory.MemoryFiles;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

/// <summary>
/// Owns a pinned Linux directory. Child operations accept a single validated component and
/// verify the opened object against the data root's mount and device.
/// </summary>
internal sealed class LinuxDirectory : IDisposable
{
    #region Static Fields

    private const int CloseOnExec = 0x80000;
    private const int PathOnly = 0x200000;
    private const int StatMask = 0x1385; // TYPE, NLINK, CTIME, INO, SIZE, MNT_ID
    // Linux UAPI open flags and syscall numbers for the repository's packaging targets.
    private static readonly (int Directory, int NoFollow, int RenameAt2, int Statx) Abi = RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => (1 << 16, 1 << 17, 316, 332),
        Architecture.Arm64 => (1 << 14, 1 << 15, 276, 291),
        Architecture.Arm => (1 << 14, 1 << 15, 382, 397),
        _ => default
    };

    #endregion

    #region Private Fields

    private readonly SafeFileHandle _handle;
    private readonly FileIdentity _identity;

    #endregion

    #region Constructors

    private LinuxDirectory(SafeFileHandle handle, FileIdentity identity)
    {
        _handle = handle;
        _identity = identity;
    }

    #endregion

    #region Properties

    #endregion

    #region Public Methods

    /// <summary>
    /// Opens the operator-configured root. Its ancestors are trusted configuration.
    /// </summary>
    /// <param name="root">The absolute persistent data directory.</param>
    /// <returns>The pinned data root.</returns>
    internal static LinuxDirectory OpenRoot(string root)
    {
        if (!OperatingSystem.IsLinux() || !BitConverter.IsLittleEndian || Abi.Directory == 0)
            throw new MemoryFileException("STORAGE_UNAVAILABLE");
        var handle = Handle(open(Path.TrimEndingDirectorySeparator(root), Abi.Directory | Abi.NoFollow | CloseOnExec));
        try
        {
            return new LinuxDirectory(handle, Inspect(handle));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens an exact child directory and optionally creates it durably.
    /// </summary>
    /// <param name="name">A single directory name.</param>
    /// <param name="create">Whether missing directories may be created.</param>
    /// <returns>The pinned child.</returns>
    internal LinuxDirectory DirectoryAt(string name, bool create = false)
    {
        if (create)
        {
            if (mkdirat(_handle, name, 0x1c0) < 0 && Marshal.GetLastPInvokeError() != 17)
                throw NativeError();
            Sync();
        }

        var handle = Handle(openat(_handle, name, Abi.Directory | Abi.NoFollow | CloseOnExec, 0));
        try
        {
            return new LinuxDirectory(handle, InspectChild(handle, name));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Inspects a child without opening devices, following links or crossing mounts.
    /// </summary>
    /// <param name="name">One exact component relative to this directory.</param>
    /// <returns>The identity of the actual named entry, or null if absent.</returns>
    internal FileIdentity? Lookup(string name)
    {
        var fd = openat(_handle, name, PathOnly | Abi.NoFollow | CloseOnExec, 0);
        if (fd < 0 && Marshal.GetLastPInvokeError() == 2)
            return null;
        using var handle = Handle(fd);
        return InspectChild(handle, name);
    }

    /// <summary>
    /// Pins and reads a single-link regular file with bounded allocation and change detection.
    /// </summary>
    /// <param name="name">The exact leaf name.</param>
    /// <param name="maximum">Maximum accepted raw byte length.</param>
    /// <param name="expected">Optional identity observed when enumerating this file.</param>
    /// <returns>The bytes and the identity checked before and after reading.</returns>
    internal (byte[] Bytes, FileIdentity Identity) Read(string name, int maximum, FileIdentity? expected = null)
    {
        using var pin = Handle(openat(_handle, name, PathOnly | Abi.NoFollow | CloseOnExec, 0));
        var before = InspectChild(pin, name);
        if (!before.IsRegular || before.Links != 1)
            throw new MemoryFileException("UNSAFE_PATH");
        if (expected is not null && before != expected)
            throw new MemoryFileException("CONFLICT");
        if (before.Size > (ulong)maximum)
            throw new MemoryFileException("CONTENT_TOO_LARGE");

        // Only reopen the kernel-pinned regular inode, never the mutable leaf name.
        using var handle = File.OpenHandle(ProcPath(pin), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[(int)before.Size];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var count = RandomAccess.Read(handle, bytes.AsSpan(offset), offset);
            if (count == 0)
                throw new MemoryFileException("CONFLICT");
            offset += count;
        }

        if (Inspect(handle) != before || Lookup(name) != before)
            throw new MemoryFileException("CONFLICT");
        return (bytes, before);
    }

    /// <summary>
    /// Creates a private, exclusive staging file in this directory.
    /// </summary>
    /// <param name="name">The server-generated staging name.</param>
    /// <returns>The writable handle owned by the caller.</returns>
    internal SafeFileHandle CreateTemporary(string name) =>
        Handle(openat(_handle, name, 1 | 0x40 | 0x80 | Abi.NoFollow | CloseOnExec, 0x180));

    /// <summary>
    /// Checks that a staging name still refers to the pinned single-link regular file.
    /// </summary>
    /// <param name="name">The private staging name.</param>
    /// <param name="handle">The still-open staging file.</param>
    /// <returns>Whether both handles identify the same safe file.</returns>
    internal bool OwnsTemporary(string name, SafeFileHandle handle)
    {
        var current = Inspect(handle);
        return current.IsRegular && current.Links == 1 && Lookup(name) == current;
    }

    /// <summary>
    /// Compares pinned directory identities across a fresh traversal.
    /// </summary>
    /// <param name="other">The freshly opened directory.</param>
    /// <returns>Whether both handles denote the same directory.</returns>
    internal bool SameDirectory(LinuxDirectory other) =>
        _identity.Mount == other._identity.Mount && _identity.Device == other._identity.Device && _identity.Inode == other._identity.Inode;

    /// <summary>
    /// Compares the directory observed before enumeration with a fresh traversal after it.
    /// </summary>
    /// <param name="current">The directory reopened from the configured root.</param>
    /// <returns>Whether the inode, mount and metadata, including change time, still match.</returns>
    internal bool MatchesSnapshot(LinuxDirectory current) => _identity == current._identity;

    /// <summary>
    /// Atomically moves a single entry between pinned directories.
    /// </summary>
    /// <param name="name">The existing source name.</param>
    /// <param name="destination">The pinned destination directory.</param>
    /// <param name="target">The exact destination name.</param>
    /// <param name="noReplace">Whether an existing destination must cause failure.</param>
    internal void Rename(string name, LinuxDirectory destination, string target, bool noReplace)
    {
        if (RenameAt2(Abi.RenameAt2, _handle, name, destination._handle, target, noReplace ? 1u : 0u) < 0)
            throw NativeError();
    }

    /// <summary>
    /// Removes a single directory entry without following it.
    /// </summary>
    /// <param name="name">The entry relative to this handle.</param>
    /// <param name="directory">Whether the entry must be an empty directory.</param>
    internal void Remove(string name, bool directory = false)
    {
        if (unlinkat(_handle, name, directory ? 0x200 : 0) < 0)
            throw NativeError();
    }

    /// <summary>
    /// Enumerates all names, including hidden files, in the pinned directory.
    /// </summary>
    /// <returns>Immediate entry names for descriptor-relative inspection.</returns>
    internal IEnumerable<string> Names()
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(ProcPath(_handle)))
        {
            var name = Path.GetFileName(entry);
            if (name.Contains('\uFFFD'))
                throw new MemoryFileException("UNSAFE_PATH");
            yield return name;
        }
    }

    /// <summary>
    /// Flushes changes to entries in this directory.
    /// </summary>
    internal void Sync()
    {
        if (fsync(_handle) < 0)
            throw NativeError();
    }

    /// <inheritdoc />
    public void Dispose() => _handle.Dispose();

    #endregion

    #region Private Methods

    private FileIdentity InspectChild(SafeFileHandle handle, string name)
    {
        var identity = Inspect(handle);
        if (identity.Mount != _identity.Mount || identity.Device != _identity.Device)
            throw new MemoryFileException("UNSAFE_PATH");
        // Also reject aliases on case-folding filesystems and entries renamed during lookup.
        var resolved = new FileInfo(ProcPath(handle)).LinkTarget;
        if (resolved is null || !string.Equals(Path.GetFileName(resolved), name, StringComparison.Ordinal))
            throw new MemoryFileException("UNSAFE_PATH");
        return identity;
    }

    private static string ProcPath(SafeFileHandle handle) => $"/proc/self/fd/{handle.DangerousGetHandle()}";

    private static FileIdentity Inspect(SafeFileHandle handle)
    {
        // statx is a fixed-size Linux UAPI structure across the supported ABIs.
        var data = new byte[256];
        if (Statx(Abi.Statx, handle, "", 0x1000, StatMask, data) < 0)
            throw NativeError();
        if ((BinaryPrimitives.ReadUInt32LittleEndian(data) & StatMask) != StatMask)
            throw new MemoryFileException("STORAGE_UNAVAILABLE");
        return new FileIdentity(BitConverter.ToUInt64(data, 144), BitConverter.ToUInt64(data, 136),
            BitConverter.ToUInt64(data, 32), BitConverter.ToUInt64(data, 40), BitConverter.ToUInt16(data, 28),
            BitConverter.ToUInt32(data, 16), BitConverter.ToInt64(data, 96), BitConverter.ToUInt32(data, 104));
    }

    private static SafeFileHandle Handle(int fd) => fd >= 0 ? new SafeFileHandle(fd, true) : throw NativeError();

    private static MemoryFileException NativeError() => new(Marshal.GetLastPInvokeError() switch
    {
        2 => "MEMORY_NOT_FOUND",
        17 => "ALREADY_EXISTS",
        18 or 20 or 21 or 40 => "UNSAFE_PATH",
        _ => "STORAGE_UNAVAILABLE"
    });

    [DllImport("libc", SetLastError = true)]
    private static extern int open(string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int openat(SafeFileHandle dir, string name, int flags, uint mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int mkdirat(SafeFileHandle dir, string name, uint mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int unlinkat(SafeFileHandle dir, string name, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int fsync(SafeFileHandle fd);

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern nint RenameAt2(nint number, SafeFileHandle source, string name, SafeFileHandle destination, string target, uint flags);

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern nint Statx(nint number, SafeFileHandle fd, string path, int flags, uint mask, [Out] byte[] buffer);

    #endregion
}

/// <summary>
/// Captures the statx fields used for mount isolation and concurrent-change checks.
/// </summary>
/// <param name="Mount">The Linux mount ID.</param>
/// <param name="Device">The pair of device numbers.</param>
/// <param name="Inode">The inode on that device.</param>
/// <param name="Size">The raw byte length.</param>
/// <param name="Mode">The type and permission bits.</param>
/// <param name="Links">The hard link count.</param>
/// <param name="ChangeSeconds">The seconds of the last inode change.</param>
/// <param name="ChangeNanoseconds">The nanoseconds of the last inode change.</param>
internal sealed record FileIdentity(ulong Mount, ulong Device, ulong Inode, ulong Size, ushort Mode, uint Links, long ChangeSeconds, uint ChangeNanoseconds)
{
    #region Static Fields

    #endregion

    #region Private Fields

    #endregion

    #region Constructors

    #endregion

    #region Properties

    /// <summary>
    /// Gets whether this entry is a regular file.
    /// </summary>
    internal bool IsRegular => (Mode & 0xf000) == 0x8000;

    /// <summary>
    /// Gets whether this entry is a directory.
    /// </summary>
    internal bool IsDirectory => (Mode & 0xf000) == 0x4000;

    #endregion

    #region Public Methods

    #endregion

    #region Private Methods

    #endregion
}
