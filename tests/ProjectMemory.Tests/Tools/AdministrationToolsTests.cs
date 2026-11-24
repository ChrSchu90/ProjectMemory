namespace ProjectMemory.Tests.Tools;

using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModelContextProtocol.Protocol;
using ProjectMemory.Database;
using ProjectMemory.Project;
using ProjectMemory.Tests.Database;
using ProjectMemory.Tests.Hosting;
using ProjectMemory.Tools;

/// <summary>
/// Tests the administration tools through isolated HTTP hosts and direct storage-failure calls.
/// </summary>
[TestClass]
public sealed class AdministrationToolsTests : ServerHostTestBase
{
    #region Static Fields

    #endregion

    #region Private Fields

    #endregion

    #region Test Constructors

    #endregion

    #region Test Preparation

    #endregion

    #region Tests

    /// <summary>
    /// Verifies that each protocol and token mode supports scoped tool calls and preserves data after rejected mutations.
    /// </summary>
    /// <param name="protocol">The MCP protocol revision used for every request.</param>
    /// <param name="token">The optional bearer token configured on the host and its authorized client.</param>
    /// <returns>A task that completes after checking the tool responses and the retained project and note.</returns>
    [TestMethod]
    [DataRow("2025-11-25", null)]
    [DataRow("2025-11-25", "test-token")]
    [DataRow("2026-07-28", null)]
    [DataRow("2026-07-28", "test-token")]
    public async Task ToolCallsAcrossProtocolsAndTokenModes(string protocol, string? token)
    {
        const string context = "local:Transport";
        const string path = "note.md";
        await using var host = await Fixture.StartAsync(token);
        using var client = host.CreateClient(token);
        var id = ProjectId(await CallToolAsync(client, "create_project", new { project_name = "transport" }, protocol));
        await CallToolAsync(client, "bind_context", new { context_id = context, project_id = id }, protocol);
        await CallToolAsync(client, "create_memory", new { context_id = context, path, content = "needle" }, protocol);
        await CallToolAsync(client, "unbind_context", new { context_id = context, project_id = id }, protocol, "INVALID_ARGUMENT");

        if (token is not null)
        {
            using var anonymous = host.CreateClient();
            using var create = await SendToolAsync(anonymous, "create_project", new { project_name = "denied" }, protocol);
            Assert.AreEqual(HttpStatusCode.Unauthorized, create.StatusCode);
            using var delete = await SendToolAsync(anonymous, "delete_project", new { project_id = id }, protocol);
            Assert.AreEqual(HttpStatusCode.Unauthorized, delete.StatusCode);
            var projects = await CallToolAsync(client, "list_projects", new { }, protocol);
            Assert.AreEqual(id, projects.GetProperty("data").GetProperty("projects").EnumerateArray().Single().GetProperty("project_id").GetString());
        }

        var search = await CallToolAsync(client, "search_memories", new { context_id = context, query = "needle" }, protocol);
        Assert.AreEqual(path, search.GetProperty("data").GetProperty("matches").EnumerateArray().Single().GetProperty("path").GetString());
    }

    /// <summary>
    /// Verifies that malformed arguments to read-only administration tools return the expected structured errors.
    /// </summary>
    /// <param name="tool">The administration operation whose arguments should be rejected.</param>
    /// <param name="arguments">The malformed argument object sent unchanged through the MCP transport.</param>
    /// <param name="error">The expected structured administration error code.</param>
    /// <returns>A task that completes after verifying the error envelope.</returns>
    [TestMethod]
    [DataRow("resolve_context", "{}", "INVALID_CONTEXT")]
    [DataRow("resolve_context", "{\"context_id\":null}", "INVALID_CONTEXT")]
    [DataRow("resolve_context", "{\"context_id\":false}", "INVALID_CONTEXT")]
    [DataRow("resolve_context", "{\"contextId\":\"chat:Workspace\"}", "INVALID_ARGUMENT")]
    [DataRow("list_projects", "{\"limit\":1.5}", "INVALID_ARGUMENT")]
    [DataRow("list_projects", "{\"limit\":2147483648}", "INVALID_ARGUMENT")]
    [DataRow("list_projects", "{\"limit\":\"50\"}", "INVALID_ARGUMENT")]
    [DataRow("list_projects", "{\"offset\":null}", "INVALID_ARGUMENT")]
    [DataRow("list_context_bindings", "{\"project_id\":null}", "INVALID_ARGUMENT")]
    [DataRow("list_context_bindings", "{\"project_id\":12}", "INVALID_ARGUMENT")]
    public async Task MalformedReadOnlyArgumentsReturnExpectedError(string tool, string arguments, string error)
    {
        await using var fixture = await Fixture.StartAsync();
        using var client = fixture.CreateClient();
        using var document = JsonDocument.Parse(arguments);
        await CallToolAsync(client, tool, document.RootElement, error: error);
    }

    /// <summary>
    /// Verifies that a rejected project mutation preserves the existing project's identity, name and status.
    /// </summary>
    /// <param name="tool">The project mutation to reject.</param>
    /// <param name="arguments">The malformed JSON arguments sent to the tool.</param>
    /// <returns>A task that completes after checking the error and the persisted project list.</returns>
    [TestMethod]
    [DataRow("create_project", "{}")]
    [DataRow("create_project", "{\"project_name\":null}")]
    [DataRow("create_project", "{\"project_name\":12}")]
    [DataRow("create_project", "{\"project_name\":\"injected\",\"project_path\":\"/tmp/injected\"}")]
    [DataRow("archive_project", "{}")]
    [DataRow("reactivate_project", "{\"project_id\":false}")]
    public async Task MalformedProjectMutationPreservesProjects(string tool, string arguments)
    {
        await using var host = await Fixture.StartAsync();
        using var client = host.CreateClient();
        var existing = await CallToolAsync(client, "create_project", new { project_name = "existing" });
        if (tool == "reactivate_project")
        {
            existing = await CallToolAsync(client, "archive_project", new { project_id = ProjectId(existing) });
        }

        using var document = JsonDocument.Parse(arguments);
        await CallToolAsync(client, tool, document.RootElement, error: "INVALID_ARGUMENT");
        var projects = await CallToolAsync(client, "list_projects", new { });
        var remaining = projects.GetProperty("data").GetProperty("projects").EnumerateArray().Single();
        Assert.IsTrue(JsonElement.DeepEquals(existing.GetProperty("data").GetProperty("project"), remaining));
    }

    /// <summary>
    /// Verifies that incomplete bind and unbind requests leave the existing active binding with its original owner.
    /// </summary>
    /// <returns>A task that completes after verifying the stored binding after each rejected request.</returns>
    [TestMethod]
    public async Task MalformedBindingMutationsPreserveBinding()
    {
        const string context = "chat:Existing";
        await using var host = await Fixture.StartAsync();
        using var client = host.CreateClient();
        var id = ProjectId(await CallToolAsync(client, "create_project", new { project_name = "existing" }));
        await CallToolAsync(client, "bind_context", new { context_id = context, project_id = id });
        foreach (var (tool, arguments, error) in new (string, object, string)[]
        {
            ("bind_context", new { project_id = id }, "INVALID_CONTEXT"),
            ("bind_context", new { context_id = "chat:New" }, "INVALID_ARGUMENT"),
            ("unbind_context", new { }, "INVALID_CONTEXT")
        })
        {
            await CallToolAsync(client, tool, arguments, error: error);
            var bindings = await CallToolAsync(client, "list_context_bindings", new { });
            var remaining = bindings.GetProperty("data").GetProperty("bindings").EnumerateArray().Single();
            Assert.AreEqual(id, remaining.GetProperty("project_id").GetString());
            Assert.AreEqual(context, remaining.GetProperty("context_id").GetString());
            Assert.IsTrue(remaining.GetProperty("active").GetBoolean());
        }
    }

    /// <summary>
    /// Verifies that project and binding changes preserve exact routing and isolation across the project lifecycle.
    /// </summary>
    /// <returns>A task that completes after verifying the workflow through MCP.</returns>
    [TestMethod]
    public async Task AdministrationWorkflow()
    {
        await using var fixture = await Fixture.StartAsync();
        using var client = fixture.CreateClient();
        var a = ProjectId(await CallToolAsync(client, "create_project", new { project_name = "a" }));

        var b = ProjectId(await CallToolAsync(client, "create_project", new { project_name = "b" }));

        Assert.AreNotEqual(a, b);
        Assert.AreNotEqual(Guid.Empty, Guid.Parse(a));
        await CallToolAsync(client, "create_project", new { project_name = "a" }, error: "ALREADY_EXISTS");

        await CallToolAsync(client, "create_project", new { project_name = "injected", project_id = a, project_path = fixture.Root }, error: "INVALID_ARGUMENT");

        var first = await CallToolAsync(client, "bind_context", new { context_id = "chat:Portal", project_id = a });

        Assert.IsTrue(first.GetProperty("data").GetProperty("changed").GetBoolean());
        var same = await CallToolAsync(client, "bind_context", new { context_id = "chat:Portal", project_id = a });

        Assert.IsFalse(same.GetProperty("data").GetProperty("changed").GetBoolean());
        await CallToolAsync(client, "bind_context", new { context_id = "chat:Portal", project_id = b }, error: "CONFLICT");

        await CallToolAsync(client, "bind_context", new { context_id = "chat:portal", project_id = b });

        await CallToolAsync(client, "bind_context", new { context_id = "local:Workspace", project_id = a });

        await CallToolAsync(client, "bind_context", new { context_id = "git:github.com/Org/Repo", project_id = a });

        await CallToolAsync(client, "bind_context", new { context_id = "git:github.com/org/repo", project_id = b });

        Assert.AreEqual(a, ResolvedId(await CallToolAsync(client, "resolve_context", new { context_id = "chat:Portal" })));

        Assert.AreEqual(b, ResolvedId(await CallToolAsync(client, "resolve_context", new { context_id = "chat:portal" })));

        await CallToolAsync(client, "resolve_context", new { context_id = "chat:PORTAL" }, error: "CONTEXT_UNBOUND");

        await CallToolAsync(client, "resolve_context", new { context_id = "git:GITHUB.COM/Org/Repo" }, error: "CONTEXT_UNBOUND");

        await CallToolAsync(client, "resolve_context", new { context_id = "LOCAL:Workspace" }, error: "INVALID_CONTEXT");

        await CallToolAsync(client, "bind_context", new { context_id = "chat:Missing", project_id = Guid.NewGuid().ToString("D") }, error: "PROJECT_NOT_FOUND");

        await CallToolAsync(client, "bind_context", new { context_id = "chat:Missing", project_id = "bad-id" }, error: "INVALID_ARGUMENT");

        await CallToolAsync(client, "unbind_context", new { context_id = "chat:Missing" }, error: "CONTEXT_UNBOUND");

        var page = await CallToolAsync(client, "list_projects", new { limit = 1 });

        Assert.IsTrue(page.GetProperty("data").GetProperty("has_more").GetBoolean());
        Assert.AreEqual(a, page.GetProperty("data").GetProperty("projects")[0].GetProperty("project_id").GetString());
        var next = await CallToolAsync(client, "list_projects", new { limit = 1, offset = 1 });

        Assert.IsFalse(next.GetProperty("data").GetProperty("has_more").GetBoolean());
        Assert.AreEqual(b, next.GetProperty("data").GetProperty("projects")[0].GetProperty("project_id").GetString());
        await CallToolAsync(client, "list_projects", new { limit = 201 }, error: "INVALID_ARGUMENT");

        await CallToolAsync(client, "list_projects", new { offset = -1 }, error: "INVALID_ARGUMENT");

        var bindings = await CallToolAsync(client, "list_context_bindings", new { project_id = a });

        Assert.AreEqual(3, bindings.GetProperty("data").GetProperty("bindings").GetArrayLength());
        Assert.IsTrue(bindings.GetProperty("data").GetProperty("bindings").EnumerateArray().All(binding => binding.GetProperty("project_id").GetString() == a));
        var archived = await CallToolAsync(client, "archive_project", new { project_id = a });

        Assert.AreEqual(a, ProjectId(archived));
        Assert.AreEqual("archived", archived.GetProperty("data").GetProperty("project").GetProperty("status").GetString());
        Assert.IsFalse((await CallToolAsync(client, "archive_project", new { project_id = a })).GetProperty("data").GetProperty("changed").GetBoolean());

        await CallToolAsync(client, "resolve_context", new { context_id = "chat:Portal" }, error: "PROJECT_ARCHIVED");

        await CallToolAsync(client, "resolve_context", new { context_id = "local:Workspace" }, error: "PROJECT_ARCHIVED");

        await CallToolAsync(client, "bind_context", new { context_id = "chat:New", project_id = a }, error: "PROJECT_ARCHIVED");

        Assert.AreEqual(b, ResolvedId(await CallToolAsync(client, "resolve_context", new { context_id = "chat:portal" })));

        Assert.AreEqual(3, (await CallToolAsync(client, "list_context_bindings", new { project_id = a })).GetProperty("data").GetProperty("bindings").GetArrayLength());

        Assert.AreEqual(a, ProjectId(await CallToolAsync(client, "reactivate_project", new { project_id = a })));

        Assert.AreEqual(a, ResolvedId(await CallToolAsync(client, "resolve_context", new { context_id = "chat:Portal" })));

        await CallToolAsync(client, "unbind_context", new { context_id = "chat:Portal" });

        await CallToolAsync(client, "resolve_context", new { context_id = "chat:Portal" }, error: "CONTEXT_UNBOUND");

        await CallToolAsync(client, "bind_context", new { context_id = "chat:Portal", project_id = b });

        Assert.AreEqual(b, ResolvedId(await CallToolAsync(client, "resolve_context", new { context_id = "chat:Portal" })));

        Assert.AreEqual(a, ResolvedId(await CallToolAsync(client, "resolve_context", new { context_id = "git:github.com/Org/Repo" })));

        Assert.AreEqual(2, (await CallToolAsync(client, "list_projects", new { })).GetProperty("data").GetProperty("projects").GetArrayLength());
    }

    /// <summary>
    /// Verifies that real host restarts preserve project IDs, bindings and archival restrictions.
    /// </summary>
    /// <param name="archived">Whether the project is archived before the host restarts.</param>
    /// <returns>A task that completes when the test has verified that real host restarts preserve project IDs, bindings and archival restrictions.</returns>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RestartPreservesRouting(bool archived)
    {
        var root = Fixture.CreateRoot();
        try
        {
            string id;
            await using (var first = await Fixture.StartAsync(root: root, ownsRoot: false))
            {
                using var client = first.CreateClient();
                id = ProjectId(await CallToolAsync(client, "create_project", new { project_name = "restart" }));

                await CallToolAsync(client, "bind_context", new { context_id = "local:Restart", project_id = id });

                if (archived)
                {
                    await CallToolAsync(client, "archive_project", new { project_id = id });
                }
            }

            await using (var second = await Fixture.StartAsync(root: root, ownsRoot: false))
            {
                using var client = second.CreateClient();
                var resolved = await CallToolAsync(client, "resolve_context", new { context_id = "local:Restart" }, error: archived ? "PROJECT_ARCHIVED" : null);

                if (!archived)
                {
                    Assert.AreEqual(id, ResolvedId(resolved));
                }

                await CallToolAsync(client, "reactivate_project", new { project_id = id });

                Assert.AreEqual(id, ResolvedId(await CallToolAsync(client, "resolve_context", new { context_id = "local:Restart" })));
            }
        }
        finally
        {
            ContextOptions.ClearPool(Path.Combine(root, "ProjectMemory.db"));
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// Verifies that registry failures become safe structured errors with identical JSON text and no fallback project.
    /// </summary>
    /// <returns>A task that completes when the test has verified that registry failures become safe structured errors with identical JSON text and no fallback project.</returns>
    [TestMethod]
    public async Task StorageFailureReturnsSafeStructuredError()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await fixture.CreateProjectAsync("a");
        await using (var context = fixture.CreateContext())
        {
            await context.Database.ExecuteSqlRawAsync("DROP TABLE ContextBindings;");
        }

        var tools = new AdministrationTools(fixture.Services.GetRequiredService<ProjectManager>(), fixture.Services.GetRequiredService<ProjectDeletion>(), NullLogger<AdministrationTools>.Instance);
        var result = await tools.ResolveContextAsync("chat:Missing");

        Assert.IsTrue(result.IsError);
        var content = result.StructuredContent!.Value;
        Assert.AreEqual("STORAGE_UNAVAILABLE", content.GetProperty("error").GetProperty("code").GetString());
        Assert.IsFalse(content.GetProperty("ok").GetBoolean());
        Assert.AreEqual(JsonValueKind.Null, content.GetProperty("data").ValueKind);
        var text = ((TextContentBlock)result.Content.Single()).Text;
        using var json = JsonDocument.Parse(text);
        Assert.IsTrue(JsonElement.DeepEquals(content, json.RootElement));
        Assert.IsFalse(text.Contains(fixture.Root, StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("SQLite", StringComparison.Ordinal));
    }

    #endregion

    #region Private Methods

    private static string ProjectId(JsonElement response) => response.GetProperty("data").GetProperty("project").GetProperty("project_id").GetString()!;

    private static string ResolvedId(JsonElement response) => response.GetProperty("data").GetProperty("project_id").GetString()!;

    #endregion

    #region Test Classes

    #endregion
}
