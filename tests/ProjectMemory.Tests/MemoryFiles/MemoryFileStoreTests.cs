namespace ProjectMemory.Tests.MemoryFiles;

using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjectMemory.Configuration;
using ProjectMemory.MemoryFiles;

/// <summary>
/// Exercises Linux descriptor, durability and validation boundaries on private fixtures.
/// </summary>
[TestClass]
public sealed class MemoryFileStoreTests
{
    #region Static Fields

    private const string PathInProject = "nested/file.md";

    #endregion

    #region Private Fields

    private readonly Guid _project = Guid.NewGuid();
    private string _root = null!;
    private string _parent = null!;
    private string _outside = null!;
    private MemoryFileStore _store = null!;
    private Action<LifecycleCheckpoint>? _observe;

    #endregion

    #region Test Constructors

    #endregion

    #region Test Preparation

    /// <summary>
    /// Creates a disposable data root and a separate sentinel directory.
    /// </summary>
    [TestInitialize]
    public void Initialize()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64 or Architecture.Arm))
            Assert.Inconclusive("The descriptor store requires Linux x64, arm64 or arm.");
        _root = Directory.CreateTempSubdirectory("pm-store-").FullName;
        _outside = Directory.CreateTempSubdirectory("pm-sentinel-").FullName;
        _parent = Path.Combine(_root, "projects", _project.ToString("D"), "nested");
        Directory.CreateDirectory(_parent);
        File.WriteAllText(Path.Combine(_outside, "file.md"), "outside");
        _store = new MemoryFileStore(Options.Create(new ProjectMemoryOptions { DataDirectory = _root }), point => _observe?.Invoke(point));
    }

    /// <summary>
    /// Removes only the directories created by this test.
    /// </summary>
    [TestCleanup]
    public void Cleanup()
    {
        if (_root is not null)
        {
            Directory.Delete(_root, true);
        }

        if (_outside is not null)
        {
            Directory.Delete(_outside, true);
        }
    }

    #endregion

    #region Tests

    /// <summary>
    /// Verifies that symlinks, multiply linked files and FIFOs cannot be read, updated or removed as memories.
    /// </summary>
    /// <param name="kind">The unsafe entry type.</param>
    [TestMethod]
    [DataRow("symlink")]
    [DataRow("fifo")]
    [DataRow("hardlink")]
    [DataRow("directory")]
    public void RefusesUnsafeLeaf(string kind)
    {
        var file = Path.Combine(_parent, "file.md");
        var outside = Path.Combine(_outside, "file.md");
        switch (kind)
        {
            case "symlink":
                File.CreateSymbolicLink(file, outside);
                break;
            case "hardlink":
                Assert.AreEqual(0, link(outside, file));
                break;
            case "directory":
                Directory.CreateDirectory(file);
                break;
            default:
                Assert.AreEqual(0, mkfifo(file, 0x180));
                break;
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData("outside"u8));
        Refused(() => _store.Read(_project, PathInProject, 1, 1), "UNSAFE_PATH");
        Refused(() => _store.Write(_project, PathInProject, "new", hash, default), "UNSAFE_PATH");
        Refused(() => _store.Write(_project, PathInProject, "new", null, default), "UNSAFE_PATH");
        Refused(() => _store.Delete(_project, PathInProject, hash, default), "UNSAFE_PATH");
        Assert.AreEqual("outside", File.ReadAllText(outside));
        Assert.HasCount(1, Directory.GetFileSystemEntries(_parent));
    }

    /// <summary>
    /// Verifies that replacing a parent or leaf during staging cannot redirect publication to a sibling.
    /// </summary>
    /// <param name="parent">Whether the parent directory is swapped.</param>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void StagedReplacementRejectsRedirect(bool parent)
    {
        var old = _store.Write(_project, PathInProject, "before", null, default);
        var file = Path.Combine(_parent, "file.md");
        _observe = point =>
        {
            if (point != LifecycleCheckpoint.Staged)
                return;
            if (parent)
            {
                Directory.Move(_parent, _parent + "-moved");
                Directory.CreateSymbolicLink(_parent, _outside);
            }
            else
            {
                File.Move(file, file + ".moved");
                File.CreateSymbolicLink(file, Path.Combine(_outside, "file.md"));
            }
        };
        Refused(() => _store.Write(_project, PathInProject, "after", old.ContentHash, default), "UNSAFE_PATH");
        Assert.AreEqual("outside", File.ReadAllText(Path.Combine(_outside, "file.md")));
        var original = parent ? Path.Combine(_parent + "-moved", "file.md") : file + ".moved";
        Assert.AreEqual("before", File.ReadAllText(original));
        Assert.IsEmpty(Directory.GetFiles(Path.GetDirectoryName(original)!, ".pm-*"));
    }

    /// <summary>
    /// Verifies that an external edit after staging wins the final hash check without being overwritten.
    /// </summary>
    [TestMethod]
    public void FinalHashDetectsManualEdit()
    {
        var old = _store.Write(_project, PathInProject, "before", null, default);
        _observe = point =>
        {
            if (point == LifecycleCheckpoint.Staged)
            {
                File.WriteAllText(Path.Combine(_parent, "file.md"), "manual");
            }
        };
        Refused(() => _store.Write(_project, PathInProject, "after", old.ContentHash, default), "CONFLICT");
        Assert.AreEqual("manual", File.ReadAllText(Path.Combine(_parent, "file.md")));
        Assert.HasCount(1, Directory.GetFileSystemEntries(_parent));
    }

    /// <summary>
    /// Verifies that an interrupted short write remains private and cleanup preserves the original bytes.
    /// </summary>
    [TestMethod]
    public void PartialStagingFileNeverReplacesOriginal()
    {
        var old = _store.Write(_project, PathInProject, "before", null, default);
        var file = Path.Combine(_parent, "file.md");
        _observe = point =>
        {
            if (point != LifecycleCheckpoint.StagingOpened)
                return;
            var staging = Directory.GetFiles(_parent, ".pm-*").Single();
            File.WriteAllText(staging, "part");
            Assert.AreEqual("before", File.ReadAllText(file));
            throw new IOException("Injected write failure");
        };
        Assert.ThrowsExactly<IOException>(() => _store.Write(_project, PathInProject, "after", old.ContentHash, default));
        Assert.AreEqual("before", File.ReadAllText(file));
        Assert.HasCount(1, Directory.GetFiles(_parent));
    }

    /// <summary>
    /// Verifies that cancellation before publication preserves the file and releases all staging resources.
    /// </summary>
    [TestMethod]
    public void CancellationBeforeCommitPreservesOriginal()
    {
        var old = _store.Write(_project, PathInProject, "before", null, default);
        using var cancelled = new CancellationTokenSource();
        _observe = point =>
        {
            if (point == LifecycleCheckpoint.Staged)
            {
                cancelled.Cancel();
            }
        };
        Assert.ThrowsExactly<OperationCanceledException>(() => _store.Write(_project, PathInProject, "after", old.ContentHash, cancelled.Token));
        Assert.AreEqual("before", File.ReadAllText(Path.Combine(_parent, "file.md")));
        Assert.HasCount(1, Directory.GetFileSystemEntries(_parent));
    }

    /// <summary>
    /// Verifies that the mount guard rejects a real kernel mount boundary using read-only descriptors.
    /// </summary>
    [TestMethod]
    public void RejectsRealMountCrossing()
    {
        // The only non-fixture observation is procfs mount metadata. No mount privileges are needed.
        using var root = LinuxDirectory.OpenRoot("/");
        Refused(() => root.DirectoryAt("proc").Dispose(), "UNSAFE_PATH");
    }

    /// <summary>
    /// Verifies that limits measure raw UTF-8 bytes and invalid text can still be hash-guardedly removed.
    /// </summary>
    [TestMethod]
    public void ByteAndEncodingLimits()
    {
        var file = Path.Combine(_parent, "file.md");
        var limit = new string('ä', MemoryFileStore.MaximumBytes / 2);
        var created = _store.Write(_project, PathInProject, limit, null, default);
        Assert.AreEqual(MemoryFileStore.MaximumBytes, new FileInfo(file).Length);
        Refused(() => _store.Write(_project, PathInProject, limit + "a", created.ContentHash, default), "CONTENT_TOO_LARGE");
        File.AppendAllText(file, "a");
        Refused(() => _store.Read(_project, PathInProject, 1, 1), "CONTENT_TOO_LARGE");
        File.WriteAllBytes(file, [0xff]);
        Refused(() => _store.Read(_project, PathInProject, 1, 1), "INVALID_ENCODING");
        _store.Delete(_project, PathInProject, Convert.ToHexStringLower(SHA256.HashData(new byte[] { 0xff })), default);
        Assert.IsFalse(File.Exists(file));
        Assert.IsFalse(MemoryFileStore.ValidPath(new string('ä', 127) + ".md"));
        Assert.IsFalse(MemoryFileStore.ValidPath(string.Concat(Enumerable.Repeat("a/", 16)) + "b.md"));
    }

    #endregion

    #region Private Methods

    private static void Refused(Action operation, string code)
    {
        var exception = Assert.ThrowsExactly<MemoryFileException>(operation);
        Assert.AreEqual(code, exception.Code);
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int link(string existing, string destination);

    [DllImport("libc", SetLastError = true)]
    private static extern int mkfifo(string name, uint mode);

    #endregion

    #region Test Classes

    #endregion
}
