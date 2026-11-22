namespace ProjectMemory.Tests.Tools;

using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjectMemory.MemoryFiles;
using ProjectMemory.Project;
using ProjectMemory.Search;
using ProjectMemory.Tests.Hosting;

/// <summary>Exercises the real ripgrep adapter and its process failure boundary on disposable fixtures.</summary>
[TestClass]
public sealed class AdvancedSearchTests : ServerHostTestBase
{
    #region Static Fields

    private const string Context = "chat:Search";

    #endregion

    #region Private Fields

    #endregion

    #region Test Constructors

    #endregion

    #region Test Preparation

    #endregion

    #region Tests

    /// <summary>Verifies real regex engines, literal defaults, OR, word and line boundaries and every projection.</summary>
    /// <returns>The asynchronous fixture verification.</returns>
    [TestMethod]
    public async Task PatternsContextAndProjections()
    {
        await using var host = await Fixture.StartAsync();
        using var client = host.CreateClient();
        await Register(client);
        await Note(client, "a.md", "\ufeffheading\r\nCAT cat\r\ncat\r\ncatfish\r\nend\r\nC++ [x].*\r\nété été\r\n-é\r\n");
        await Note(client, "z.md", "other\n");
        async Task<JsonElement> Find(string query, object match, object output) =>
            (await CallToolAsync(client, "search_memories", new { context_id = Context, query, match, output })).GetProperty("data");

        var andContext = await Find("cat", new { boundary = "word", alternatives = new[] { "other" } }, new { before = 1, after = 1 });
        var rows = andContext.GetProperty("matches").EnumerateArray().ToArray();
        Assert.HasCount(3, rows);
        Assert.AreEqual(2, rows[0].GetProperty("line").GetInt32());
        Assert.AreEqual(2, rows[0].GetProperty("submatches").GetArrayLength());
        Assert.AreEqual(4, rows[0].GetProperty("submatches")[1].GetProperty("start_byte").GetInt32());
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4 }, andContext.GetProperty("context").EnumerateArray().Select(c => c.GetProperty("line").GetInt32()).ToArray());
        var counts = (await Find("cat", new { boundary = "word" }, new { view = "counts" })).GetProperty("counts")[0];
        Assert.AreEqual(2, counts.GetProperty("records").GetInt32());
        Assert.AreEqual(3, counts.GetProperty("occurrences").GetInt32());
        var capped = await Find("cat", new { alternatives = new[] { "other" } }, new { view = "files", limit = 1, max_hits_per_file = 1 });
        Assert.IsTrue(capped.GetProperty("limited_by_request").GetBoolean());
        Assert.IsTrue(capped.GetProperty("has_more").GetBoolean());
        Assert.AreEqual("a.md", capped.GetProperty("files")[0].GetString());
        var last = await Find("cat", new { alternatives = new[] { "other" } }, new { view = "files", offset = 1 });
        Assert.AreEqual("z.md", last.GetProperty("files")[0].GetString());
        Assert.IsFalse(last.GetProperty("has_more").GetBoolean());
        var unicode = (await Find("ÉTÉ", new { }, new { })).GetProperty("matches")[0];
        Assert.AreEqual(6, unicode.GetProperty("submatches")[1].GetProperty("start_byte").GetInt32());
        foreach (var pair in new[] { ("CAT", "smart"), ("cat", "sensitive") })
        {
            var exact = await Find(pair.Item1, new { @case = pair.Item2, boundary = "line" }, new { });
            Assert.AreEqual(pair.Item1 == "CAT" ? 0 : 1, exact.GetProperty("matches").GetArrayLength());
        }

        var regex = await Find(@"cat\r?\ncatfish", new { syntax = "regex", multiline = true }, new { });
        var block = regex.GetProperty("matches")[0];
        Assert.AreEqual(3, block.GetProperty("line").GetInt32());
        Assert.AreEqual(4, block.GetProperty("end_line").GetInt32());
        var pcre = await Find(@"(?<=CAT )cat", new { syntax = "regex", engine = "pcre2", @case = "sensitive" }, new { });
        Assert.AreEqual(1, pcre.GetProperty("matches").GetArrayLength());
        var literal = await Find("C++ [x].*", new { }, new { });
        Assert.AreEqual(6, literal.GetProperty("matches")[0].GetProperty("line").GetInt32());
        var inverse = await Find("cat", new { invert = true }, new { view = "counts" });
        Assert.AreEqual(5, inverse.GetProperty("counts")[0].GetProperty("records").GetInt32());
        Assert.AreEqual(0, inverse.GetProperty("counts")[0].GetProperty("occurrences").GetInt32());
        var dash = await Find("-é", new { }, new { });
        Assert.AreEqual(1, dash.GetProperty("matches").GetArrayLength());
    }

    /// <summary>
    /// Verifies that nullable search options and bounded array arguments follow the advertised schema without losing error handling.
    /// </summary>
    /// <returns>A task that completes after successful searches and structured rejections at the array boundary.</returns>
    [TestMethod]
    public async Task NullableSearchOptionsAndArrayBoundaries()
    {
        await using var host = await Fixture.StartAsync();
        using var client = host.CreateClient();
        await Register(client);
        await Note(client, "one.md", "needle\n");

        var nullable = await CallToolAsync(client, "search_memories", new
        {
            context_id = Context, query = "needle", file = (string?)null,
            match = (object?)null, files = (object?)null, output = (object?)null
        });
        Assert.AreEqual(1, nullable.GetProperty("data").GetProperty("matches").GetArrayLength());
        var nestedNull = await CallToolAsync(client, "search_memories", new { context_id = Context, query = "needle", match = new { alternatives = (string[]?)null } });
        Assert.AreEqual(1, nestedNull.GetProperty("data").GetProperty("matches").GetArrayLength());

        var alternatives = Enumerable.Repeat("unmatched", SearchRequest.MaximumListItems).ToArray();
        var maximum = await CallToolAsync(client, "search_memories", new { context_id = Context, query = "needle", match = new { alternatives } });
        Assert.AreEqual(1, maximum.GetProperty("data").GetProperty("matches").GetArrayLength());
        await CallToolAsync(client, "search_memories", new { context_id = Context, query = "needle", match = new { alternatives = alternatives.Append("extra").ToArray() } }, error: "INVALID_ARGUMENT");

        var globs = Enumerable.Repeat("*.md", SearchRequest.MaximumListItems).ToArray();
        var included = await CallToolAsync(client, "search_memories", new { context_id = Context, query = "needle", files = new { include = globs } });
        Assert.AreEqual(1, included.GetProperty("data").GetProperty("matches").GetArrayLength());
        await CallToolAsync(client, "search_memories", new { context_id = Context, query = "needle", files = new { include = globs.Append("extra.md").ToArray() } }, error: "INVALID_ARGUMENT");
        await CallToolAsync(client, "search_memories", new { context_id = Context, query = "needle", files = new { exclude = globs.Append("extra.md").ToArray() } }, error: "INVALID_ARGUMENT");
        await CallToolAsync(client, "search_memories", new { context_id = Context, query = (string?)null }, error: "INVALID_ARGUMENT");
        await CallToolAsync(client, "search_memories", new { context_id = (string?)null, query = "needle" }, error: "INVALID_CONTEXT");
    }

    /// <summary>Verifies selection and ignore intersection across a stopped-volume corpus of 120 nested memories.</summary>
    /// <returns>The asynchronous fixture verification.</returns>
    [TestMethod]
    public async Task CorpusFiltersAndScope()
    {
        var root = Fixture.CreateRoot();
        try
        {
            string project;
            await using (var first = await Fixture.StartAsync(root: root, ownsRoot: false))
            {
                using var client = first.CreateClient();
                project = await Register(client);
            }

            var docs = Path.Combine(root, "projects", project, "docs");
            for (var i = 0; i < 120; i++)
            {
                var dir = Directory.CreateDirectory(Path.Combine(docs, i % 2 == 0 ? "api" : "legacy"));
                File.WriteAllText(Path.Combine(dir.FullName, $"note-{i:D3}.md"), $"ADR {i:D3}\nAuthorization: token [{i}]\nRésumé\n");
            }

            File.WriteAllText(Path.Combine(docs, ".ignore"), "legacy/\n");
            File.WriteAllText(Path.Combine(docs, ".gitignore"), "api/note-000.md\n");
            File.WriteAllText(Path.Combine(docs, ".rgignore"), "api/note-002.md\n");
            File.WriteAllText(Path.Combine(docs, ".hidden.md"), "Authorization");
            File.WriteAllText(Path.Combine(docs, "outside.txt"), "Authorization");
            File.WriteAllBytes(Path.Combine(docs, "binary.md"), "Authorization\0"u8.ToArray());
            File.WriteAllBytes(Path.Combine(docs, "invalid.md"), [65, 0xff]);
            File.WriteAllText(Path.Combine(docs, "large.md"), new string('A', MemoryFileStore.MaximumBytes + 1));
            await using var host = await Fixture.StartAsync(root: root, ownsRoot: false);
            using var api = host.CreateClient();
            async Task<string[]> Files(object filters) =>
                (await CallToolAsync(api, "search_memories", new { context_id = Context, folder = "docs", query = "Authorization",
                    files = filters, output = new { view = "files", limit = 200 } })).GetProperty("data").GetProperty("files").EnumerateArray().Select(v => v.GetString()!).ToArray();
            Assert.HasCount(120, await Files(new { }));
            Assert.IsEmpty(await Files(new { max_depth = 1 }));
            Assert.HasCount(58, await Files(new { respect_ignore_files = true, include = new[] { "**/*.md" } }));
            CollectionAssert.AreEqual(new[] { "docs/api/note-010.md", "docs/api/note-012.md" },
                await Files(new { include = new[] { "**/note-01[0-3].md" }, exclude = new[] { "legacy/**" } }));
            var one = await CallToolAsync(api, "search_memories", new { context_id = Context, file = "docs/api/note-010.md",
                query = @"ADR \d+", match = new { syntax = "regex", alternatives = new[] { "résumé" } }, output = new { after = 1 } });
            Assert.AreEqual(2, one.GetProperty("data").GetProperty("matches").GetArrayLength());
            await CallToolAsync(api, "search_memories", new { context_id = Context, query = "a", file = "../bad.md" }, error: "INVALID_PATH");
            await CallToolAsync(api, "search_memories", new { context_id = Context, query = "a", file = "docs/API/note-010.md" }, error: "MEMORY_NOT_FOUND");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// Verifies that malformed options, out-of-range pages and invalid patterns return their expected search errors.
    /// </summary>
    /// <returns>A task that completes after verifying the error envelopes.</returns>
    [TestMethod]
    public async Task InvalidAdvancedArguments()
    {
        await using var host = await Fixture.StartAsync();
        using var client = host.CreateClient();
        await Register(client);
        foreach (var options in new object[] { new { syntax = "raw" }, new { engine = "pcre2" }, new { syntax = "regex", multiline = true, invert = true },
            new { alternatives = new string[9] }, new { unknown = true }, new { invert = "true" }, new { alternatives = new[] { 2 } } })
        {
            await CallToolAsync(client, "search_memories", new { context_id = Context, query = "a", match = options }, error: "INVALID_ARGUMENT");
        }

        foreach (var options in new object[] { new { view = "files", after = 1 }, new { before = -1 }, new { limit = 0 }, new { limit = 201 },
            new { offset = -1 }, new { offset = 100001 }, new { offset = 1.5 }, new { max_hits_per_file = 0 } })
        {
            await CallToolAsync(client, "search_memories", new { context_id = Context, query = "a", output = options }, error: "INVALID_ARGUMENT");
        }

        await CallToolAsync(client, "search_memories", new { context_id = Context, query = "a", folder = "x", file = "a.md" }, error: "INVALID_ARGUMENT");
        await CallToolAsync(client, "search_memories", new { context_id = Context, query = "a", files = new { include = new[] { "[oops" } } }, error: "INVALID_PATTERN");
        foreach (var engine in new[] { "default", "pcre2" })
        {
            await CallToolAsync(client, "search_memories", new { context_id = Context, query = "(", match = new { syntax = "regex", engine } }, error: "INVALID_PATTERN");
        }
    }

    /// <summary>Verifies bounded process and protocol failures, including invalid binary path and excess pipe data.</summary>
    /// <returns>The asynchronous fixture verification.</returns>
    [TestMethod]
    public async Task ProcessFailures()
    {
        var root = Fixture.CreateRoot();
        try
        {
            async Task Fails(string executable, string[] args, string code)
            {
                var error = await Assert.ThrowsExactlyAsync<MemoryFileException>(() => new RipgrepProcess(executable)
                    .RunAsync(root, args, 10, _ => { }, default));
                Assert.AreEqual(code, error.Code);
            }

            await Fails(Path.Combine(root, "missing"), [], "SEARCH_UNAVAILABLE");
            await Fails("/bin/sh", ["-c", "exit 2"], "SEARCH_FAILED");
            await Fails("/bin/sh", ["-c", "head -c 300000 /dev/zero"], "SCAN_LIMIT");
            await Fails("/bin/sh", ["-c", "head -c 50000 /dev/zero >&2"], "SCAN_LIMIT");
            await Fails("/bin/sh", ["-c", "yes x | head -n 21000"], "SCAN_LIMIT");
            await Fails("/bin/sh", ["-c", "awk 'BEGIN { for (i=0;i<5000;i++) printf \"%01024d\\n\", i }'"], "SCAN_LIMIT");
            await Fails("/bin/sh", ["-c", "printf unfinished"], "SEARCH_PROTOCOL");
            var results = new RipgrepResults(new("a", "", null, new(), new(), new()));
            foreach (var bad in new[] { "invalid", """{"type":"other","data":{}}""", """{"type":"begin","data":{"path":{"text":"../other.md"}}}""",
                """{"type":"begin","data":{"path":{"bytes":"/w=="}}}""" })
            {
                var batch = results.BeginBatch(["a.md"]);
                var error = Assert.ThrowsExactly<MemoryFileException>(() => batch.Accept(Encoding.UTF8.GetBytes(bad)));
                Assert.AreEqual("SEARCH_PROTOCOL", error.Code);
            }

            var valid = results.BeginBatch(["a.md"]);
            valid.Accept("""{"type":"begin","data":{"path":{"bytes":"YS5tZA=="}}}"""u8.ToArray());
            valid.Accept("""{"type":"match","data":{"path":{"text":"a.md"},"line_number":1,"absolute_offset":0,"lines":{"bytes":"w6kK"},"submatches":[{"match":{"bytes":"w6k="},"start":0,"end":2}]}}"""u8.ToArray());
            valid.Accept("""{"type":"end","data":{"path":{"text":"a.md"},"binary_offset":null,"stats":{"matches":1}}}"""u8.ToArray());
            valid.Accept("""{"type":"summary","data":{"stats":{"searches":1,"matches":1}}}"""u8.ToArray());
            valid.Complete(0);
            Assert.AreEqual("é", results.Page(default).Matches.Single().Excerpt);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>Verifies four concurrent child slots, queued cancellation, independent project writes and deadline cleanup.</summary>
    /// <returns>The asynchronous fixture verification.</returns>
    [TestMethod]
    public async Task ChildAdmissionAndReaping()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("The process fixture uses the Linux deployment runtime.");
            return;
        }

        var temp = Fixture.CreateRoot();
        try
        {
            var executable = Path.Combine(temp, "wait");
            await File.WriteAllTextAsync(executable, $"#!/bin/sh\n/bin/sleep 60 &\necho $! > '{temp}/pid-'$$.tmp\n/bin/mv '{temp}/pid-'$$.tmp '{temp}/pid-'$$.ready\nwait\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await using var host = await Fixture.StartAsync();
            using var client = host.CreateClient();
            var id = await Register(client);
            await Note(client, "one.md", "needle");
            var projects = host.Services.GetRequiredService<ProjectManager>();
            var other = (await projects.CreateProjectAsync("Other")).Data!.Project.ProjectId.ToString();
            await projects.BindAsync("local:other", other);
            var manager = host.Services.GetRequiredService<MemoryManager>();
            var files = host.Services.GetRequiredService<MemoryFileStore>();
            using var search = new RipgrepSearchService(manager, files, executable, TimeSpan.FromSeconds(20));
            var query = new SearchRequest("needle", "", null, new(), new(), new());
            using var stop = new CancellationTokenSource();
            var active = Enumerable.Range(0, 4).Select(_ => search.RunAsync(Context, query, stop.Token)).ToArray();
            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            while (Directory.GetFiles(temp, "*.ready").Length < 4)
            {
                await Task.Delay(10, watchdog.Token);
            }

            try
            {
                var writer = projects.BindAsync("chat:alias", id);
                Assert.IsFalse(writer.IsCompleted);
                using var waiting = new CancellationTokenSource();
                var extra = search.RunAsync("local:other", query, waiting.Token);
                Assert.IsFalse(extra.IsCompleted);
                Assert.IsTrue((await projects.BindAsync("chat:other", other).WaitAsync(watchdog.Token)).Ok);
                waiting.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(() => extra);
                Assert.HasCount(4, Directory.GetFiles(temp, "*.ready"));
                stop.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(() => Task.WhenAll(active));
                Assert.IsTrue((await writer.WaitAsync(watchdog.Token)).Ok);
            }
            finally
            {
                stop.Cancel();
            }

            using var shortSearch = new RipgrepSearchService(manager, files, executable, TimeSpan.FromMilliseconds(800));
            var timed = await Assert.ThrowsExactlyAsync<MemoryFileException>(() => shortSearch.RunAsync(Context, query, default));
            Assert.AreEqual("SEARCH_TIMEOUT", timed.Code);
            Assert.HasCount(5, Directory.GetFiles(temp, "*.ready"));
            foreach (var marker in Directory.GetFiles(temp, "*.ready"))
            {
                var parent = int.Parse(Path.GetFileNameWithoutExtension(marker)[4..]);
                Assert.IsFalse(Directory.Exists($"/proc/{parent}"), "Direct child must have been reaped.");
                var child = (await File.ReadAllTextAsync(marker)).Trim();
                var stat = $"/proc/{child}/stat";
                if (File.Exists(stat))
                {
                    Assert.AreEqual('Z', File.ReadAllText(stat).Split(' ')[2][0], "Descendant must no longer be running.");
                }
            }

            Assert.IsTrue((await projects.BindAsync("chat:after-timeout", id)).Ok);
            await File.WriteAllTextAsync(executable, "#!/bin/sh\nexec /usr/bin/rg \"$@\"\n");
            Assert.IsTrue((await search.RunAsync(Context, query, default)).Ok);
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    #endregion

    #region Private Methods

    private static async Task<string> Register(HttpClient client)
    {
        var reply = await CallToolAsync(client, "create_project", new { project_name = "Corpus" });
        var id = reply.GetProperty("data").GetProperty("project").GetProperty("project_id").GetString()!;
        await CallToolAsync(client, "bind_context", new { context_id = Context, project_id = id });
        return id;
    }

    private static Task<JsonElement> Note(HttpClient client, string path, string content) =>
        CallToolAsync(client, "create_memory", new { context_id = Context, path, content });

    #endregion

    #region Test Classes

    #endregion
}
