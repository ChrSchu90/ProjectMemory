namespace ProjectMemory.Tests.Tools;

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjectMemory.MemoryFiles;
using ProjectMemory.Project;
using ProjectMemory.Routing;
using ProjectMemory.Tests.Hosting;

/// <summary>
/// Exercises the complete memory contract against isolated HTTP hosts, SQLite and real directories.
/// </summary>
[TestClass]
public sealed class MemoryToolsTests : ServerHostTestBase
{
    #region Static Fields

    private const string Context = "chat:Alpha";
    private const string Note = "Sub Folder/Nested/Case.md";

    #endregion

    #region Private Fields

    #endregion

    #region Test Constructors

    #endregion

    #region Test Preparation

    /// <summary>
    /// Checks the Linux descriptor ABI required by these filesystem workflows.
    /// </summary>
    [TestInitialize]
    public void CheckPlatform()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64 or Architecture.Arm))
            Assert.Inconclusive("This workflow requires a supported Linux descriptor ABI.");
    }

    #endregion

    #region Tests

    /// <summary>
    /// Verifies that each protocol and authentication mode supports exact Markdown CRUD and project deletion.
    /// </summary>
    /// <param name="protocol">The transport revision.</param>
    /// <param name="token">The optional shared bearer.</param>
    /// <returns>The asynchronous verification.</returns>
    [TestMethod]
    [DataRow("2025-11-25", null)]
    [DataRow("2026-07-28", null)]
    [DataRow("2025-11-25", "test-token")]
    [DataRow("2026-07-28", "test-token")]
    public async Task MemoryWorkflow(string protocol, string? token)
    {
        await using var fixture = await Fixture.StartAsync(token);
        using var client = fixture.CreateClient(token);
        var id = await ProjectAsync(client, Context);
        var sibling = await ProjectAsync(client, "chat:alpha");
        await CallToolAsync(client, "bind_context", new { context_id = "local:alias", project_id = id }, protocol);
        var args = new { context_id = Context, path = Note };
        var file = Path.Combine(fixture.Root, "projects", id, Note);
        var created = await CallToolAsync(client, "create_memory", new { args.context_id, args.path, content = "first" }, protocol);
        Assert.AreEqual(Hash("first"u8.ToArray()), HashFrom(created));
        await CallToolAsync(client, "create_memory", new { args.context_id, args.path, content = "other" }, protocol, "ALREADY_EXISTS");
        Assert.AreEqual("first", File.ReadAllText(file));
        await CallToolAsync(client, "read_memory", new { context_id = "chat:alpha", path = Note }, protocol, "MEMORY_NOT_FOUND");
        await CallToolAsync(client, "read_memory", new { context_id = "chat:ALPHA", path = Note }, protocol, "CONTEXT_UNBOUND");
        await CallToolAsync(client, "read_memory", new { context_id = "CHAT:Alpha", path = Note }, protocol, "INVALID_CONTEXT");

        var raw = Encoding.UTF8.GetBytes("\ufeffone\r\ntwö\nfinal");
        await File.WriteAllBytesAsync(file, raw);
        var targeted = await CallToolAsync(client, "read_memory", new { context_id = "local:alias", path = Note, start_line = 2, line_count = 1 }, protocol);
        Assert.AreEqual("twö\n", targeted.GetProperty("data").GetProperty("content").GetString());
        Assert.AreEqual(Hash(raw), HashFrom(targeted));
        var full = await CallToolAsync(client, "read_memory", args, protocol);
        Assert.AreEqual(Encoding.UTF8.GetString(raw), full.GetProperty("data").GetProperty("content").GetString());
        await CallToolAsync(client, "update_memory", new { args.context_id, args.path, expected_content_hash = HashFrom(created), content = "lost" }, protocol, "CONFLICT");
        await CallToolAsync(client, "delete_memory", new { args.context_id, args.path, expected_content_hash = HashFrom(created) }, protocol, "CONFLICT");
        CollectionAssert.AreEqual(raw, await File.ReadAllBytesAsync(file));
        var updated = await CallToolAsync(client, "update_memory", new { args.context_id, args.path, content = "replacement", expected_content_hash = Hash(raw) }, protocol);
        Assert.AreEqual("replacement", await File.ReadAllTextAsync(file));
        File.Move(file, file + ".md");
        await CallToolAsync(client, "read_memory", args, protocol, "MEMORY_NOT_FOUND");
        await CallToolAsync(client, "delete_memory", new { args.context_id, path = Note + ".md", expected_content_hash = HashFrom(updated) }, protocol);
        Assert.IsFalse(File.Exists(file + ".md"));
        File.WriteAllText(file, "manual");
        await CallToolAsync(client, "read_memory", args, protocol);
        File.Delete(file);
        await CallToolAsync(client, "read_memory", args, protocol, "MEMORY_NOT_FOUND");

        await CallToolAsync(client, "create_memory", new { context_id = "chat:alpha", path = Note, content = "sibling" }, protocol);
        await CallToolAsync(client, "archive_project", new { project_id = id }, protocol);
        await CallToolAsync(client, "create_memory", new { args.context_id, args.path, content = "denied" }, protocol, "PROJECT_ARCHIVED");
        if (token is not null)
        {
            using var unauthorized = fixture.CreateClient();
            using var denied = await SendAsync(unauthorized, $$$$"""{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"delete_project","arguments":{"project_id":"{{{{id}}}}"}}}""", protocol);
            Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
        }

        await CallToolAsync(client, "delete_project", new { project_id = id, confirm = true }, protocol, "INVALID_ARGUMENT");
        await CallToolAsync(client, "delete_project", new { project_id = id }, protocol);
        await CallToolAsync(client, "delete_project", new { project_id = id }, protocol, "PROJECT_NOT_FOUND");
        await CallToolAsync(client, "read_memory", args, protocol, "CONTEXT_UNBOUND");
        await CallToolAsync(client, "resolve_context", new { context_id = "local:alias" }, protocol, "CONTEXT_UNBOUND");
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Root, "projects", id)));
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Root, "quarantine", id)));
        Assert.AreEqual("sibling", await File.ReadAllTextAsync(Path.Combine(fixture.Root, "projects", sibling, Note)));
    }

    /// <summary>
    /// Verifies that addressing rejects ambiguous or escaping paths without creating project files.
    /// </summary>
    /// <param name="path">The refused path.</param>
    /// <returns>The asynchronous verification.</returns>
    [TestMethod]
    [DataRow("../outside.md")]
    [DataRow("ok/../outside.md")]
    [DataRow("/outside.md")]
    [DataRow("C:/outside.md")]
    [DataRow("a\\b.md")]
    [DataRow("a//b.md")]
    [DataRow("a/./b.md")]
    [DataRow(".pm-note.md")]
    [DataRow(".git/config.md")]
    [DataRow("Bad /note.md")]
    [DataRow("note.MD")]
    [DataRow("note.md/")]
    public async Task PathsFailClosed(string path)
    {
        await using var fixture = await Fixture.StartAsync();
        using var client = fixture.CreateClient();
        await ProjectAsync(client, Context);
        await CallToolAsync(client, "create_memory", new { context_id = Context, path, content = "x" }, error: "INVALID_PATH");
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Root, "projects")));
    }

    /// <summary>
    /// Verifies that a staging failure preserves the old file and cleans every private temporary entry.
    /// </summary>
    /// <returns>The asynchronous verification.</returns>
    [TestMethod]
    public async Task StagingFailurePreservesBytes()
    {
        var fail = false;
        await using var fixture = await StartWithCheckpointAsync(point =>
        {
            if (fail && point == LifecycleCheckpoint.Staged)
                throw new IOException("private-host-diagnostic");
        });
        using var client = fixture.CreateClient();
        var id = await ProjectAsync(client, Context);
        var old = await CreateAsync(client, Context, "original");
        fail = true;
        var result = await CallToolAsync(client, "update_memory", new { context_id = Context, path = Note, content = "new", expected_content_hash = old }, error: "STORAGE_UNAVAILABLE");
        var file = Path.Combine(fixture.Root, "projects", id, Note);
        Assert.AreEqual("original", await File.ReadAllTextAsync(file));
        Assert.HasCount(1, Directory.GetFileSystemEntries(Path.GetDirectoryName(file)!));
        Assert.IsFalse(result.GetRawText().Contains("private-host-diagnostic", StringComparison.Ordinal));
    }

    /// <summary>
    /// Verifies that a stopped dataset including an interrupted deletion restores into a fresh root and recovers.
    /// </summary>
    /// <param name="at">The durable deletion boundary to interrupt.</param>
    /// <returns>The asynchronous restore verification.</returns>
    [TestMethod]
    [DataRow("IntentCommitted")]
    [DataRow("Quarantined")]
    [DataRow("PurgeEntry")]
    [DataRow("Purged")]
    public async Task FullColdRestoreResumesInterruptedDeletion(string at)
    {
        var original = Fixture.CreateRoot();
        var backup = Fixture.CreateRoot();
        var restored = Fixture.CreateRoot();
        try
        {
            var stop = Enum.Parse<LifecycleCheckpoint>(at);
            var raw = Encoding.UTF8.GetBytes("\ufeffRestored ä\r\n");
            string keep, archived, deleting;
            await using (var host = await StartWithCheckpointAsync(point =>
            {
                if (point == stop)
                    throw new IOException("isolated interruption");
            }, original, false))
            {
                using var client = host.CreateClient();
                keep = await ProjectAsync(client, Context);
                archived = await ProjectAsync(client, "chat:alpha");
                deleting = await ProjectAsync(client, "local:delete");
                await CallToolAsync(client, "bind_context", new { context_id = "git:Host/Owner/Repo", project_id = keep });
                await CreateAsync(client, Context, "seed");
                await File.WriteAllBytesAsync(Path.Combine(original, "projects", keep, Note), raw);
                await CreateAsync(client, "chat:alpha", "archived");
                await CallToolAsync(client, "archive_project", new { project_id = archived });
                await CreateAsync(client, "local:delete", "doomed");
                await CallToolAsync(client, "create_memory", new { context_id = "local:delete", path = "second.md", content = "doomed too" });
                await CallToolAsync(client, "delete_project", new { project_id = deleting }, error: "RECOVERY_REQUIRED");
                await CallToolAsync(client, "reactivate_project", new { project_id = deleting }, error: "PROJECT_BLOCKED");
                await CallToolAsync(client, "bind_context", new { project_id = deleting, context_id = "local:new" }, error: "PROJECT_BLOCKED");
                await CallToolAsync(client, "read_memory", new { context_id = "local:delete", path = Note }, error: "PROJECT_ARCHIVED");
            }

            Assert.AreEqual(1L, Scalar(original, "SELECT COUNT(*) FROM ProjectRoutings WHERE DeletionPending=1 AND Status='Archived'"));
            Assert.AreEqual(stop == LifecycleCheckpoint.IntentCommitted, Directory.Exists(Path.Combine(original, "projects", deleting)));
            Assert.AreEqual(stop is LifecycleCheckpoint.Quarantined or LifecycleCheckpoint.PurgeEntry, Directory.Exists(Path.Combine(original, "quarantine", deleting)));
            Directory.CreateDirectory(Path.Combine(original, "operator", "empty"));
            File.WriteAllBytes(Path.Combine(original, "operator", ".marker"), [0, 255]);
            CopyColdDataset(original, backup);
            CopyColdDataset(backup, restored);
            await using var restarted = await Fixture.StartAsync(root: restored, ownsRoot: false);
            using var recovered = restarted.CreateClient();
            Assert.IsTrue(Directory.Exists(Path.Combine(restored, "operator", "empty")));
            CollectionAssert.AreEqual(new byte[] { 0, 255 }, File.ReadAllBytes(Path.Combine(restored, "operator", ".marker")));
            var active = await CallToolAsync(recovered, "resolve_context", new { context_id = Context });
            Assert.AreEqual(keep, active.GetProperty("data").GetProperty("project_id").GetString());
            await CallToolAsync(recovered, "resolve_context", new { context_id = "chat:ALPHA" }, error: "CONTEXT_UNBOUND");
            var read = await CallToolAsync(recovered, "read_memory", new { context_id = "git:Host/Owner/Repo", path = Note });
            Assert.AreEqual(Hash(raw), HashFrom(read));
            Assert.AreEqual(Encoding.UTF8.GetString(raw), read.GetProperty("data").GetProperty("content").GetString());
            await CallToolAsync(recovered, "read_memory", new { context_id = "chat:alpha", path = Note }, error: "PROJECT_ARCHIVED");
            var reactivated = await CallToolAsync(recovered, "reactivate_project", new { project_id = archived });
            Assert.AreEqual(archived, reactivated.GetProperty("data").GetProperty("project").GetProperty("project_id").GetString());
            var archivedRead = await CallToolAsync(recovered, "read_memory", new { context_id = "chat:alpha", path = Note });
            Assert.AreEqual("archived", archivedRead.GetProperty("data").GetProperty("content").GetString());
            await CallToolAsync(recovered, "resolve_context", new { context_id = "local:delete" }, error: "CONTEXT_UNBOUND");
            await CallToolAsync(recovered, "delete_project", new { project_id = deleting }, error: "PROJECT_NOT_FOUND");
            Assert.AreEqual(2L, Scalar(restored, "SELECT COUNT(*) FROM ProjectRoutings"));
            Assert.AreEqual(3L, Scalar(restored, "SELECT COUNT(*) FROM ContextBindings"));
            Assert.AreEqual(0L, Scalar(restored, "SELECT COUNT(*) FROM ProjectRoutings WHERE DeletionPending"));
            Assert.IsFalse(Directory.Exists(Path.Combine(restored, "projects", deleting)));
            Assert.IsFalse(Directory.Exists(Path.Combine(restored, "quarantine", deleting)));
        }
        finally
        {
            Directory.Delete(original, true);
            Directory.Delete(backup, true);
            Directory.Delete(restored, true);
        }
    }

    /// <summary>
    /// Verifies that an obsolete resolved route cannot authorize access after archive, rebind or deletion.
    /// </summary>
    /// <param name="operation">The intervening registry change.</param>
    /// <returns>The asynchronous race verification.</returns>
    [TestMethod]
    [DataRow("archive")]
    [DataRow("delete")]
    [DataRow("rebind")]
    public async Task RevalidatesPreviouslyResolvedProject(string operation)
    {
        using var release = new ManualResetEventSlim(false);
        var routed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await StartWithCheckpointAsync(point =>
        {
            if (point == LifecycleCheckpoint.Routed)
            {
                routed.TrySetResult();
                Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(10)));
            }
        });
        using var client = fixture.CreateClient();
        var id = await ProjectAsync(client, Context);
        var other = await ProjectAsync(client, "local:sibling");
        var error = operation switch { "archive" => "PROJECT_ARCHIVED", "delete" => "CONTEXT_UNBOUND", _ => "CONFLICT" };
        var waiting = CallToolAsync(client, "create_memory", new { context_id = Context, path = Note, content = "obsolete" }, error: error);
        try
        {
            await routed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (operation == "rebind")
            {
                await CallToolAsync(client, "unbind_context", new { context_id = Context });
                await CallToolAsync(client, "bind_context", new { context_id = Context, project_id = other });
            }
            else
            {
                await CallToolAsync(client, operation + "_project", new { project_id = id });
            }
        }
        finally
        {
            release.Set();
        }

        await waiting;
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "projects", id, Note)));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "projects", other, Note)));
    }

    /// <summary>
    /// Verifies that failures in the private quarantine keep the project blocked across restart and permit a safe retry.
    /// </summary>
    /// <returns>The asynchronous recovery verification.</returns>
    [TestMethod]
    public async Task UnsafeQuarantineRequiresOperatorRepair()
    {
        var root = Fixture.CreateRoot();
        var sentinel = Fixture.CreateRoot();
        try
        {
            var outside = Path.Combine(sentinel, "outside.md");
            File.WriteAllText(outside, "preserve");
            string id;
            await using (var host = await Fixture.StartAsync(root: root, ownsRoot: false))
            {
                using var client = host.CreateClient();
                id = await ProjectAsync(client, Context);
                await CreateAsync(client, Context, "note");
                var link = Path.Combine(root, "projects", id, "unsafe.md");
                File.CreateSymbolicLink(link, outside);
                await CallToolAsync(client, "delete_project", new { project_id = id }, error: "RECOVERY_REQUIRED");
            }

            await using (var restarted = await Fixture.StartAsync(root: root, ownsRoot: false))
            {
                using var client = restarted.CreateClient();
                await CallToolAsync(client, "reactivate_project", new { project_id = id }, error: "PROJECT_BLOCKED");
                await CallToolAsync(client, "create_memory", new { context_id = Context, path = Note, content = "denied" }, error: "PROJECT_ARCHIVED");
                Assert.AreEqual("preserve", File.ReadAllText(outside));
            }

            // The operator inspects and removes only the offending link while stopped.
            File.Delete(Path.Combine(root, "quarantine", id, "unsafe.md"));
            await using var repaired = await Fixture.StartAsync(root: root, ownsRoot: false);
            using var recovered = repaired.CreateClient();
            await CallToolAsync(recovered, "delete_project", new { project_id = id }, error: "PROJECT_NOT_FOUND");
            Assert.AreEqual(0L, Scalar(root, "SELECT COUNT(*) FROM ContextBindings"));
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "quarantine", id)));
            Assert.AreEqual("preserve", File.ReadAllText(outside));
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(sentinel, true);
        }
    }

    /// <summary>
    /// Verifies that an in-flight authorized file operation completes before archive or project cleanup takes effect.
    /// </summary>
    /// <param name="admin">The administrative operation racing with the write.</param>
    /// <returns>The asynchronous coordination verification.</returns>
    [TestMethod]
    [DataRow("archive_project")]
    [DataRow("delete_project")]
    public async Task AdministrationWaitsForAuthorizedWrite(string admin)
    {
        using var release = new ManualResetEventSlim();
        var staged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await StartWithCheckpointAsync(point =>
        {
            if (point == LifecycleCheckpoint.Staged)
            {
                staged.TrySetResult();
                Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(10)));
            }
        });
        using var client = fixture.CreateClient();
        var id = await ProjectAsync(client, Context);
        var write = CreateAsync(client, Context, "completed");
        await staged.Task.WaitAsync(TimeSpan.FromSeconds(10));
        async Task<bool> ChangeAsync()
        {
            if (admin == "archive_project")
                return (await fixture.Services.GetRequiredService<ProjectManager>().SetProjectStatusAsync(id, ProjectStatus.Archived)).Ok;
            return (await fixture.Services.GetRequiredService<ProjectDeletion>().DeleteAsync(id, default)).Ok;
        }

        // Both manager entry points reach the project lock before their first database await.
        var change = ChangeAsync();
        try
        {
            Assert.IsFalse(change.IsCompleted);
            Assert.AreEqual(1L, Scalar(fixture.Root, "SELECT COUNT(*) FROM ProjectRoutings WHERE Status='Active' AND DeletionPending=0"));
        }
        finally
        {
            release.Set();
        }

        Assert.AreEqual(Hash("completed"u8.ToArray()), await write);
        Assert.IsTrue(await change);
        var file = Path.Combine(fixture.Root, "projects", id, Note);
        if (admin == "archive_project")
        {
            Assert.AreEqual("completed", File.ReadAllText(file));
            await CallToolAsync(client, "read_memory", new { context_id = Context, path = Note }, error: "PROJECT_ARCHIVED");
        }
        else
        {
            Assert.IsFalse(File.Exists(file));
            Assert.AreEqual(0L, Scalar(fixture.Root, "SELECT COUNT(*) FROM ContextBindings"));
        }
    }

    /// <summary>
    /// Verifies that invalid parameters and missing trees have deterministic outcomes without implicit state creation.
    /// </summary>
    /// <returns>The asynchronous contract verification.</returns>
    [TestMethod]
    public async Task ValidatesMutationAndRangeArguments()
    {
        await using var host = await Fixture.StartAsync();
        using var client = host.CreateClient();
        var id = await ProjectAsync(client, Context);
        var args = new { context_id = Context, path = Note };
        await CallToolAsync(client, "delete_project", new { project_id = Guid.Empty }, error: "INVALID_ARGUMENT");
        await CallToolAsync(client, "delete_project", new { project_id = "bad-id" }, error: "INVALID_ARGUMENT");
        await CallToolAsync(client, "delete_project", new { project_id = Guid.NewGuid() }, error: "PROJECT_NOT_FOUND");
        await CallToolAsync(client, "delete_memory", args, error: "INVALID_ARGUMENT");
        await CallToolAsync(client, "delete_memory", new { args.context_id, args.path, expected_content_hash = "bad" }, error: "INVALID_ARGUMENT");
        await CallToolAsync(client, "update_memory", new { args.context_id, args.path, content = "" }, error: "INVALID_ARGUMENT");
        await CallToolAsync(client, "create_memory", new { args.path, content = "no context" }, error: "INVALID_CONTEXT");
        await CallToolAsync(client, "read_memory", new { args.context_id, args.path, start_line = 0 }, error: "INVALID_ARGUMENT");
        await CallToolAsync(client, "read_memory", new { args.context_id, args.path, line_count = 1.5 }, error: "INVALID_ARGUMENT");
        await CallToolAsync(client, "read_memory", new { args.context_id, args.path, line_count = 0 }, error: "INVALID_ARGUMENT");
        Assert.IsFalse(Directory.Exists(Path.Combine(host.Root, "projects")));
        await CallToolAsync(client, "delete_project", new { project_id = id });
        Assert.AreEqual(0L, Scalar(host.Root, "SELECT COUNT(*) FROM ProjectRoutings"));
        Assert.AreEqual(0L, Scalar(host.Root, "SELECT COUNT(*) FROM ContextBindings"));
    }

    /// <summary>
    /// Verifies that competing conditional operations on an exact path have at most one winner.
    /// </summary>
    /// <param name="second">The second conditional operation.</param>
    /// <returns>The asynchronous race verification.</returns>
    [TestMethod]
    [DataRow("update_memory")]
    [DataRow("delete_memory")]
    public async Task ConditionalOperationsSerialize(string second)
    {
        await using var fixture = await Fixture.StartAsync();
        using var client = fixture.CreateClient();
        var id = await ProjectAsync(client, Context);
        var hash = await CreateAsync(client, Context, "old");
        async Task<JsonElement> Compete(string tool, string content)
        {
            object arguments = tool == "delete_memory" ? new { context_id = Context, path = Note, expected_content_hash = hash }
                : new { context_id = Context, path = Note, expected_content_hash = hash, content };
            var json = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 32, method = "tools/call", @params = new { name = tool, arguments } });
            using var response = await SendAsync(client, json);
            return (await ReadResultAsync(response)).GetProperty("structuredContent");
        }

        var results = await Task.WhenAll(Compete("update_memory", "first"), Compete(second, "second"));
        var winner = results.Single(result => result.GetProperty("ok").GetBoolean());
        var loser = results.Single(result => !result.GetProperty("ok").GetBoolean());
        var file = Path.Combine(fixture.Root, "projects", id, Note);
        if (File.Exists(file))
        {
            Assert.AreEqual("CONFLICT", loser.GetProperty("error").GetProperty("code").GetString());
            Assert.AreEqual(Hash(await File.ReadAllBytesAsync(file)), HashFrom(winner));
        }
        else
        {
            Assert.AreEqual("delete_memory", second);
            Assert.AreEqual(hash, HashFrom(winner));
            Assert.AreEqual("MEMORY_NOT_FOUND", loser.GetProperty("error").GetProperty("code").GetString());
        }
    }

    #endregion

    #region Private Methods

    private static long Scalar(string root, string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "ProjectMemory.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        db.Open();
        using var query = db.CreateCommand();
        query.CommandText = sql;
        return (long)query.ExecuteScalar()!;
    }

    private static void CopyColdDataset(string source, string destination)
    {
        Assert.IsEmpty(Directory.GetFileSystemEntries(destination));
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            File.Copy(file, target);
            CollectionAssert.AreEqual(File.ReadAllBytes(file), File.ReadAllBytes(target));
        }
    }

    private static Task<Fixture> StartWithCheckpointAsync(Action<LifecycleCheckpoint> callback, string? root = null, bool ownsRoot = true) =>
        Fixture.StartAsync(root: root, ownsRoot: ownsRoot, configure: services => services.AddSingleton(callback));

    private static async Task<string> ProjectAsync(HttpClient client, string context)
    {
        var project = await CallToolAsync(client, "create_project", new { project_name = context });
        var id = project.GetProperty("data").GetProperty("project").GetProperty("project_id").GetString()!;
        await CallToolAsync(client, "bind_context", new { context_id = context, project_id = id });
        return id;
    }

    private static async Task<string> CreateAsync(HttpClient client, string context, string content) =>
        HashFrom(await CallToolAsync(client, "create_memory", new { context_id = context, path = Note, content }));

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string HashFrom(JsonElement response) => response.GetProperty("data").GetProperty("content_hash").GetString()!;

    #endregion

    #region Test Classes

    #endregion
}
