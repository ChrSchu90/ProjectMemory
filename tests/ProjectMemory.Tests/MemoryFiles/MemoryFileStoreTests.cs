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
        if (kind != "directory")
        {
            Refused(() => _store.List(_project, "", 0, 50, default), "UNSAFE_PATH");
            Refused(() => _store.Search(_project, "outside", "", 0, 50, default), "UNSAFE_PATH");
        }
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
        Refused(() => root.Lookup("proc"), "UNSAFE_PATH");
    }

    /// <summary>
    /// Verifies that changing a candidate after inspection discards the list or search result.
    /// </summary>
    /// <param name="search">Whether the change occurs before reading search content.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NavigationDetectsChangedCandidate(bool search)
    {
        _store.Write(_project, PathInProject, "original", null, default);
        _observe = point =>
        {
            if (point == (search ? LifecycleCheckpoint.SearchRead : LifecycleCheckpoint.ScanEntry))
            {
                File.AppendAllText(Path.Combine(_parent, "file.md"), "-changed");
            }
        };
        if (search)
        {
            Refused(() => _store.Search(_project, "changed", "", 0, 50, default), "CONFLICT");
        }
        else
        {
            Refused(() => _store.List(_project, "", 0, 50, default), "CONFLICT");
        }
    }

    /// <summary>
    /// Verifies that a renamed directory cannot substitute an external subtree during enumeration or content retrieval.
    /// </summary>
    /// <param name="search">Whether to redirect immediately before a search read.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NavigationRevalidatesSubtree(bool search)
    {
        _store.Write(_project, PathInProject, "inside", null, default);
        _observe = point =>
        {
            if (point == (search ? LifecycleCheckpoint.SearchRead : LifecycleCheckpoint.ScanEntry))
            {
                Directory.Move(_parent, _parent + "-moved");
                Directory.CreateSymbolicLink(_parent, _outside);
            }
        };
        if (search)
        {
            Refused(() => _store.Search(_project, "outside", "nested", 0, 1, default), "UNSAFE_PATH");
        }
        else
        {
            Refused(() => _store.List(_project, "nested", 0, 1, default), "UNSAFE_PATH");
        }
    }

    /// <summary>
    /// Verifies that cancellation during a scan propagates and the subsequent operation succeeds.
    /// </summary>
    /// <param name="search">Whether to interrupt content retrieval or directory listing.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NavigationCancellation(bool search)
    {
        _store.Write(_project, PathInProject, "needle", null, default);
        using var cancelled = new CancellationTokenSource();
        _observe = point =>
        {
            if (point == (search ? LifecycleCheckpoint.SearchRead : LifecycleCheckpoint.ScanEntry))
            {
                cancelled.Cancel();
            }
        };
        if (search)
        {
            Assert.ThrowsExactly<OperationCanceledException>(() => _store.Search(_project, "needle", "", 0, 50, cancelled.Token));
        }
        else
        {
            Assert.ThrowsExactly<OperationCanceledException>(() => _store.List(_project, "", 0, 50, cancelled.Token));
        }

        _observe = null;
        CollectionAssert.AreEqual(new[] { PathInProject }, _store.List(_project, "", 0, 50, default).Paths);
    }

    /// <summary>
    /// Verifies that search discloses unsearchable files and returns bounded excerpts containing the literal without splitting Unicode pairs.
    /// </summary>
    [TestMethod]
    public void NavigationSearchBoundsAndSkippedFiles()
    {
        var query = new string('x', MemoryFileStore.MaximumQueryLength);
        _store.Write(_project, PathInProject, "first\n" + new string('a', 100) + "🐈" + new string('z', 31) + query + new string('z', 32) + "🐈" + new string('z', 200), null, default);
        File.WriteAllBytes(Path.Combine(_parent, "invalid.md"), [0xff]);
        using (var large = File.Create(Path.Combine(_parent, "large.md")))
        {
            large.SetLength(MemoryFileStore.MaximumBytes + 1);
        }

        var page = _store.Search(_project, query, "", 0, 1, default);
        Assert.AreEqual(2, page.SkippedFiles);
        Assert.IsFalse(page.HasMore);
        var hit = page.Matches.Single();
        Assert.AreEqual(PathInProject, hit.Path);
        Assert.AreEqual(2, hit.Line);
        Assert.IsTrue(hit.ExcerptTruncated);
        Assert.Contains(query, hit.Excerpt);
        Assert.IsLessThanOrEqualTo(MemoryFileStore.MaximumExcerptLength, hit.Excerpt.Length);
        Assert.IsFalse(char.IsLowSurrogate(hit.Excerpt[0]));
        Assert.IsFalse(char.IsHighSurrogate(hit.Excerpt[^1]));
        Assert.HasCount(3, _store.List(_project, "", 0, 50, default).Paths);
    }

    /// <summary>
    /// Verifies that even ignored directory entries consume scan budget and that a smaller subtree remains usable.
    /// </summary>
    [TestMethod]
    public void NavigationEntryBudget()
    {
        var empty = Directory.CreateDirectory(Path.Combine(_parent, "empty"));
        for (var i = 1; i < MemoryFileStore.MaximumEntries; i++)
        {
            File.WriteAllText(Path.Combine(_parent, "." + i), "");
        }

        Assert.IsEmpty(_store.List(_project, "nested", 0, 50, default).Paths);
        File.WriteAllText(Path.Combine(_parent, ".extra"), "");
        Refused(() => _store.List(_project, "nested", 0, 50, default), "SCAN_LIMIT");
        Refused(() => _store.Search(_project, "needle", "nested", 0, 50, default), "SCAN_LIMIT");
        Assert.IsEmpty(_store.List(_project, "nested/" + empty.Name, 0, 50, default).Paths);
    }

    /// <summary>
    /// Verifies that a full page contains the first 200 files in ordinal order and the last file remains reachable.
    /// </summary>
    [TestMethod]
    public void NavigationMaximumPageSize()
    {
        var paths = Enumerable.Range(0, 201).Select(i => $"nested/{i:D3}.md").ToArray();
        foreach (var path in paths.Reverse())
        {
            File.WriteAllText(Path.Combine(_parent, Path.GetFileName(path)), "hit");
        }

        var list = _store.List(_project, "", 0, 200, default);
        var search = _store.Search(_project, "hit", "", 0, 200, default);
        Assert.IsTrue(list.HasMore);
        Assert.IsTrue(search.HasMore);
        CollectionAssert.AreEqual(paths[..200], list.Paths);
        CollectionAssert.AreEqual(list.Paths, search.Matches.Select(hit => hit.Path).ToArray());
        list = _store.List(_project, "", 200, 200, default);
        search = _store.Search(_project, "hit", "", 200, 200, default);
        Assert.IsFalse(list.HasMore);
        Assert.IsFalse(search.HasMore);
        Assert.AreEqual(paths[^1], list.Paths.Single());
        Assert.AreEqual(paths[^1], search.Matches.Single().Path);
    }

    /// <summary>
    /// Verifies that the aggregate byte budget includes nonmatches and prevents an incomplete negative answer.
    /// </summary>
    [TestMethod]
    public void NavigationSearchByteBudget()
    {
        for (var i = 0; i < MemoryFileStore.MaximumSearchBytes / MemoryFileStore.MaximumBytes; i++)
        {
            using var stream = File.Create(Path.Combine(_parent, i + ".md"));
            stream.SetLength(MemoryFileStore.MaximumBytes);
        }

        var exact = _store.Search(_project, "needle", "", 0, 1, default);
        Assert.IsEmpty(exact.Matches);
        Assert.AreEqual(0, exact.SkippedFiles);
        File.WriteAllText(Path.Combine(_parent, "extra.md"), "a");
        Refused(() => _store.Search(_project, "needle", "", 0, 1, default), "SCAN_LIMIT");
    }

    /// <summary>
    /// Verifies that a delayed final read cannot publish a search page after the cooperative deadline.
    /// </summary>
    [TestMethod]
    public void NavigationDeadlineStopsSearch()
    {
        _store.Write(_project, PathInProject, "needle", null, default);
        _observe = point =>
        {
            if (point == LifecycleCheckpoint.SearchRead)
            {
                Thread.Sleep(TimeSpan.FromSeconds(5.1));
            }
        };
        Refused(() => _store.Search(_project, "needle", "", 0, 1, default), "SCAN_LIMIT");
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
