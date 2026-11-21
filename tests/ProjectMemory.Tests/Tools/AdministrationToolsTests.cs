namespace ProjectMemory.Tests.Tools;

using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using System;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModelContextProtocol.Protocol;
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
    /// Verifies that malformed flat arguments return stable administration errors without creating projects or bindings.
    /// </summary>
    /// <param name="tool">The administration operation whose arguments should be rejected.</param>
    /// <param name="arguments">The malformed argument object sent unchanged through the MCP transport.</param>
    /// <param name="error">The expected structured administration error code.</param>
    /// <returns>A task that completes when both protocol revisions reject the arguments without registry mutation.</returns>
    [TestMethod]
    [DataRow("create_project", "{}", "INVALID_ARGUMENT")]
    [DataRow("create_project", "{\"project_name\":null}", "INVALID_ARGUMENT")]
    [DataRow("create_project", "{\"project_name\":12}", "INVALID_ARGUMENT")]
    [DataRow("create_project", "{\"project_name\":\"injected\",\"project_path\":\"/tmp/injected\"}", "INVALID_ARGUMENT")]
    [DataRow("archive_project", "{}", "INVALID_ARGUMENT")]
    [DataRow("reactivate_project", "{\"project_id\":false}", "INVALID_ARGUMENT")]
    [DataRow("resolve_context", "{}", "INVALID_CONTEXT")]
    [DataRow("resolve_context", "{\"context_id\":null}", "INVALID_CONTEXT")]
    [DataRow("resolve_context", "{\"context_id\":false}", "INVALID_CONTEXT")]
    [DataRow("resolve_context", "{\"contextId\":\"chat:Workspace\"}", "INVALID_ARGUMENT")]
    [DataRow("bind_context", "{\"project_id\":\"00000000-0000-0000-0000-000000000001\"}", "INVALID_CONTEXT")]
    [DataRow("bind_context", "{\"context_id\":\"chat:Workspace\"}", "INVALID_ARGUMENT")]
    [DataRow("unbind_context", "{}", "INVALID_CONTEXT")]
    [DataRow("list_projects", "{\"limit\":1.5}", "INVALID_ARGUMENT")]
    [DataRow("list_projects", "{\"limit\":2147483648}", "INVALID_ARGUMENT")]
    [DataRow("list_projects", "{\"limit\":\"50\"}", "INVALID_ARGUMENT")]
    [DataRow("list_projects", "{\"offset\":null}", "INVALID_ARGUMENT")]
    [DataRow("list_context_bindings", "{\"project_id\":null}", "INVALID_ARGUMENT")]
    [DataRow("list_context_bindings", "{\"project_id\":12}", "INVALID_ARGUMENT")]
    public async Task AdministrationRejectsMalformedArguments(string tool, string arguments, string error)
    {
        await using var fixture = await Fixture.StartAsync();
        using var client = fixture.CreateClient();
        using var document = JsonDocument.Parse(arguments);
        foreach (var protocol in new[] { "2025-11-25", "2026-07-28" })
        {
            await CallToolAsync(client, tool, document.RootElement, protocol, error);
            var projects = await CallToolAsync(client, "list_projects", new { }, protocol);

            Assert.AreEqual(0, projects.GetProperty("data").GetProperty("projects").GetArrayLength());
            var bindings = await CallToolAsync(client, "list_context_bindings", new { }, protocol);

            Assert.AreEqual(0, bindings.GetProperty("data").GetProperty("bindings").GetArrayLength());
        }
    }

    /// <summary>
    /// Verifies that the project and context administration tools preserve exact contexts, many-to-one isolation and lifecycle behavior through both HTTP revisions and auth modes.
    /// </summary>
    /// <param name="token">The optional bearer token configured for the host and authorized requests.</param>
    /// <param name="protocol">The MCP protocol revision used for the administration requests.</param>
    /// <returns>A task that completes when the test has verified that the project and context administration tools preserve exact contexts, many-to-one isolation and lifecycle behavior through both HTTP revisions and auth modes.</returns>
    [TestMethod]
    [DataRow(null, "2025-11-25")]
    [DataRow(null, "2026-07-28")]
    [DataRow("isolated-m2-token", "2025-11-25")]
    [DataRow("isolated-m2-token", "2026-07-28")]
    public async Task AdministrationWorkflow(string? token, string protocol)
    {
        await using var fixture = await Fixture.StartAsync(token);
        using var client = fixture.CreateClient(token);
        var a = ProjectId(await CallToolAsync(client, "create_project", new { project_name = "a" }, protocol));

        var b = ProjectId(await CallToolAsync(client, "create_project", new { project_name = "b" }, protocol));

        Assert.AreNotEqual(a, b);
        Assert.AreNotEqual(Guid.Empty, Guid.Parse(a));
        await CallToolAsync(client, "create_project", new { project_name = "a" }, protocol, "ALREADY_EXISTS");

        await CallToolAsync(client, "create_project", new { project_name = "injected", project_id = a, project_path = fixture.Root }, protocol, "INVALID_ARGUMENT");

        var first = await CallToolAsync(client, "bind_context", new { context_id = "chat:Portal", project_id = a }, protocol);

        Assert.IsTrue(first.GetProperty("data").GetProperty("changed").GetBoolean());
        var same = await CallToolAsync(client, "bind_context", new { context_id = "chat:Portal", project_id = a }, protocol);

        Assert.IsFalse(same.GetProperty("data").GetProperty("changed").GetBoolean());
        await CallToolAsync(client, "bind_context", new { context_id = "chat:Portal", project_id = b }, protocol, "CONFLICT");

        await CallToolAsync(client, "bind_context", new { context_id = "chat:portal", project_id = b }, protocol);

        await CallToolAsync(client, "bind_context", new { context_id = "local:Workspace", project_id = a }, protocol);

        await CallToolAsync(client, "bind_context", new { context_id = "git:github.com/Org/Repo", project_id = a }, protocol);

        await CallToolAsync(client, "bind_context", new { context_id = "git:github.com/org/repo", project_id = b }, protocol);

        Assert.AreEqual(a, ResolvedId(await CallToolAsync(client, "resolve_context", new { context_id = "chat:Portal" }, protocol)));

        Assert.AreEqual(b, ResolvedId(await CallToolAsync(client, "resolve_context", new { context_id = "chat:portal" }, protocol)));

        await CallToolAsync(client, "resolve_context", new { context_id = "chat:PORTAL" }, protocol, "CONTEXT_UNBOUND");

        await CallToolAsync(client, "resolve_context", new { context_id = "git:GITHUB.COM/Org/Repo" }, protocol, "CONTEXT_UNBOUND");

        await CallToolAsync(client, "resolve_context", new { }, protocol, "INVALID_CONTEXT");

        await CallToolAsync(client, "resolve_context", new { context_id = "LOCAL:Workspace" }, protocol, "INVALID_CONTEXT");

        await CallToolAsync(client, "bind_context", new { context_id = "chat:Missing", project_id = Guid.NewGuid().ToString("D") }, protocol, "PROJECT_NOT_FOUND");

        await CallToolAsync(client, "bind_context", new { context_id = "chat:Missing", project_id = "bad-id" }, protocol, "INVALID_ARGUMENT");

        await CallToolAsync(client, "unbind_context", new { context_id = "chat:Missing" }, protocol, "CONTEXT_UNBOUND");

        var page = await CallToolAsync(client, "list_projects", new { limit = 1 }, protocol);

        Assert.IsTrue(page.GetProperty("data").GetProperty("has_more").GetBoolean());
        Assert.AreEqual(a, page.GetProperty("data").GetProperty("projects")[0].GetProperty("project_id").GetString());
        var next = await CallToolAsync(client, "list_projects", new { limit = 1, offset = 1 }, protocol);

        Assert.IsFalse(next.GetProperty("data").GetProperty("has_more").GetBoolean());
        Assert.AreEqual(b, next.GetProperty("data").GetProperty("projects")[0].GetProperty("project_id").GetString());
        await CallToolAsync(client, "list_projects", new { limit = 201 }, protocol, "INVALID_ARGUMENT");

        await CallToolAsync(client, "list_projects", new { offset = -1 }, protocol, "INVALID_ARGUMENT");

        await CallToolAsync(client, "list_projects", new { limit = 1.5 }, protocol, "INVALID_ARGUMENT");

        var bindings = await CallToolAsync(client, "list_context_bindings", new { project_id = a }, protocol);

        Assert.AreEqual(3, bindings.GetProperty("data").GetProperty("bindings").GetArrayLength());
        Assert.IsTrue(bindings.GetProperty("data").GetProperty("bindings").EnumerateArray().All(binding => binding.GetProperty("project_id").GetString() == a));
        var archived = await CallToolAsync(client, "archive_project", new { project_id = a }, protocol);

        Assert.AreEqual(a, ProjectId(archived));
        Assert.AreEqual("archived", archived.GetProperty("data").GetProperty("project").GetProperty("status").GetString());
        Assert.IsFalse((await CallToolAsync(client, "archive_project", new { project_id = a }, protocol)).GetProperty("data").GetProperty("changed").GetBoolean());

        await CallToolAsync(client, "resolve_context", new { context_id = "chat:Portal" }, protocol, "PROJECT_ARCHIVED");

        await CallToolAsync(client, "resolve_context", new { context_id = "local:Workspace" }, protocol, "PROJECT_ARCHIVED");

        await CallToolAsync(client, "bind_context", new { context_id = "chat:New", project_id = a }, protocol, "PROJECT_ARCHIVED");

        Assert.AreEqual(b, ResolvedId(await CallToolAsync(client, "resolve_context", new { context_id = "chat:portal" }, protocol)));

        Assert.AreEqual(3, (await CallToolAsync(client, "list_context_bindings", new { project_id = a }, protocol)).GetProperty("data").GetProperty("bindings").GetArrayLength());

        Assert.AreEqual(a, ProjectId(await CallToolAsync(client, "reactivate_project", new { project_id = a }, protocol)));

        Assert.AreEqual(a, ResolvedId(await CallToolAsync(client, "resolve_context", new { context_id = "chat:Portal" }, protocol)));

        await CallToolAsync(client, "unbind_context", new { context_id = "chat:Portal" }, protocol);

        await CallToolAsync(client, "resolve_context", new { context_id = "chat:Portal" }, protocol, "CONTEXT_UNBOUND");

        await CallToolAsync(client, "bind_context", new { context_id = "chat:Portal", project_id = b }, protocol);

        Assert.AreEqual(b, ResolvedId(await CallToolAsync(client, "resolve_context", new { context_id = "chat:Portal" }, protocol)));

        Assert.AreEqual(a, ResolvedId(await CallToolAsync(client, "resolve_context", new { context_id = "git:github.com/Org/Repo" }, protocol)));

        Assert.AreEqual(2, (await CallToolAsync(client, "list_projects", new { }, protocol)).GetProperty("data").GetProperty("projects").GetArrayLength());

        if (token is not null)
        {
            using var unauthorized = fixture.CreateClient();
            using var denied = await SendAsync(unauthorized, """{"jsonrpc":"2.0","id":44,"method":"tools/call","params":{"name":"create_project","arguments":{"project_name":"denied"}}}""", protocol);
            Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
            Assert.AreEqual(2, (await CallToolAsync(client, "list_projects", new { }, protocol)).GetProperty("data").GetProperty("projects").GetArrayLength());
        }
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
            SqliteConnection.ClearAllPools();
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

        var tools = new AdministrationTools(fixture.Services.GetRequiredService<ProjectManager>(), NullLogger<AdministrationTools>.Instance);
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

    private static async Task<JsonElement> CallToolAsync(HttpClient client, string name, object arguments, string protocol = "2025-11-25", string? error = null)
    {
        var body = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 21, method = "tools/call", @params = new { name, arguments } });

        using var response = await SendAsync(client, body, protocol);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var result = await ReadResultAsync(response);
        var content = result.GetProperty("structuredContent");
        Assert.AreEqual("1.0", content.GetProperty("contract_version").GetString());
        Assert.AreEqual(error is null, content.GetProperty("ok").GetBoolean());
        Assert.AreEqual(error is not null, result.TryGetProperty("isError", out var flag) && flag.GetBoolean());
        using var fallback = JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!);
        Assert.IsTrue(JsonElement.DeepEquals(content, fallback.RootElement));
        if (error is not null)
        {
            Assert.AreEqual(error, content.GetProperty("error").GetProperty("code").GetString());
            Assert.AreEqual(JsonValueKind.Null, content.GetProperty("data").ValueKind);
        }
        else
        {
            Assert.AreEqual(JsonValueKind.Null, content.GetProperty("error").ValueKind);
        }

        return content;
    }

    #endregion

    #region Test Classes

    #endregion
}
