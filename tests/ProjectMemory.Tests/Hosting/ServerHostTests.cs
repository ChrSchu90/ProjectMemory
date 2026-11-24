namespace ProjectMemory.Tests.Hosting;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjectMemory.Database;
using ProjectMemory.Hosting;
using ProjectMemory.Search;
using Serilog.Core;
using Serilog.Events;

/// <summary>
/// Exercises real Kestrel HTTP boundaries using disposable local storage.
/// </summary>
[TestClass]
public sealed class ServerHostTests : ServerHostTestBase
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
    /// Verifies that MCP initializes and lists no unimplemented tools with optional auth enabled or disabled.
    /// </summary>
    /// <param name="token">The optional bearer token used by the server and client.</param>
    /// <returns>A task that completes when the test has verified that MCP initializes and lists no unimplemented tools with optional auth enabled or disabled.</returns>
    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("isolated-test-token")]
    public async Task McpHandshakeAndDiscovery(string? token)
    {
        await using var fixture = await Fixture.StartAsync(token);
        using var client = fixture.CreateClient(token);
        using var init = await SendAsync(client, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"contract-test","version":"1"}}}""");
        Assert.AreEqual(HttpStatusCode.OK, init.StatusCode);
        Assert.IsFalse(init.Headers.Contains("Mcp-Session-Id"));
        var result = await ReadResultAsync(init);
        Assert.AreEqual("ProjectMemory", result.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.AreEqual("2025-11-25", result.GetProperty("protocolVersion").GetString());
        Assert.IsTrue(result.GetProperty("capabilities").TryGetProperty("tools", out _));
        using var notification = await SendAsync(client, """{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        Assert.AreEqual(HttpStatusCode.Accepted, notification.StatusCode);
        using var list = await SendAsync(client, """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""");
        Assert.AreEqual(HttpStatusCode.OK, list.StatusCode);
        AssertToolDiscovery((await ReadResultAsync(list)).GetProperty("tools"));
        using var call = await SendAsync(client, """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"unknown_tool","arguments":{}}}""");
        var error = await ReadMessageAsync(call);
        Assert.AreEqual(3, error.GetProperty("id").GetInt32());
        Assert.IsFalse(error.TryGetProperty("result", out _));
        Assert.IsLessThan(0, error.GetProperty("error").GetProperty("code").GetInt32());
        Assert.IsFalse(string.IsNullOrEmpty(error.GetProperty("error").GetProperty("message").GetString()));
    }

    /// <summary>
    /// Verifies that search discovery exposes the same bounds used by runtime validation and the exact context ID pattern.
    /// </summary>
    /// <returns>A task that completes after inspecting the published MCP schemas.</returns>
    [TestMethod]
    public async Task SearchSchemaPublishesSharedLimitsAndExactContextPattern()
    {
        await using var fixture = await Fixture.StartAsync();
        using var client = fixture.CreateClient();
        using var response = await SendAsync(client, """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var tools = (await ReadResultAsync(response)).GetProperty("tools").EnumerateArray().ToArray();
        var search = tools.Single(tool => tool.GetProperty("name").GetString() == "search_memories");
        var properties = search.GetProperty("inputSchema").GetProperty("properties");
        var resolve = tools.Single(tool => tool.GetProperty("name").GetString() == "resolve_context");
        Assert.AreEqual(resolve.GetProperty("inputSchema").GetProperty("properties").GetProperty("context_id").GetProperty("pattern").GetString(),
            properties.GetProperty("context_id").GetProperty("pattern").GetString());

        Assert.AreEqual(SearchRequest.MaximumPatternLength, properties.GetProperty("query").GetProperty("maxLength").GetInt32());
        Assert.AreEqual(SearchRequest.MaximumPathLength, properties.GetProperty("folder").GetProperty("maxLength").GetInt32());
        Assert.AreEqual(SearchRequest.MaximumPathLength, properties.GetProperty("file").GetProperty("maxLength").GetInt32());
        var match = properties.GetProperty("match").GetProperty("properties");
        var files = properties.GetProperty("files").GetProperty("properties");
        var output = properties.GetProperty("output").GetProperty("properties");
        foreach (var array in new[] { match.GetProperty("alternatives"), files.GetProperty("include"), files.GetProperty("exclude") })
        {
            Assert.AreEqual(SearchRequest.MaximumListItems, array.GetProperty("maxItems").GetInt32());
        }

        Assert.AreEqual(SearchRequest.MaximumPatternLength, match.GetProperty("alternatives").GetProperty("items").GetProperty("maxLength").GetInt32());
        Assert.AreEqual(SearchRequest.MaximumGlobLength, files.GetProperty("include").GetProperty("items").GetProperty("maxLength").GetInt32());
        Assert.AreEqual(SearchRequest.MaximumGlobLength, files.GetProperty("exclude").GetProperty("items").GetProperty("maxLength").GetInt32());
        Assert.AreEqual(SearchRequest.MaximumDepth, files.GetProperty("max_depth").GetProperty("maximum").GetInt32());
        Assert.AreEqual(SearchRequest.MaximumContextLines, output.GetProperty("before").GetProperty("maximum").GetInt32());
        Assert.AreEqual(SearchRequest.MaximumContextLines, output.GetProperty("after").GetProperty("maximum").GetInt32());
        Assert.AreEqual(SearchRequest.MaximumOffset, output.GetProperty("offset").GetProperty("maximum").GetInt32());
        Assert.AreEqual(SearchRequest.MaximumPageLimit, output.GetProperty("limit").GetProperty("maximum").GetInt32());
        Assert.AreEqual(SearchRequest.DefaultPageLimit, output.GetProperty("limit").GetProperty("default").GetInt32());
        Assert.AreEqual(SearchRequest.MaximumHitsPerFile, output.GetProperty("max_hits_per_file").GetProperty("maximum").GetInt32());
        foreach (var tool in tools)
        {
            Assert.AreEqual("1.0", tool.GetProperty("outputSchema").GetProperty("properties").GetProperty("contract_version").GetProperty("const").GetString());
        }

        await CallToolAsync(client, "list_projects", new { });
        await CallToolAsync(client, "list_projects", new { limit = 0 }, error: "INVALID_ARGUMENT");
    }

    /// <summary>
    /// Verifies that the newer stateless protocol discovers capabilities and lists tools without an initialize handshake.
    /// </summary>
    /// <param name="token">The optional bearer token used by the server and client.</param>
    /// <returns>A task that completes when the test has verified that the newer stateless protocol discovers capabilities and lists tools without an initialize handshake.</returns>
    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("isolated-test-token")]
    public async Task StatelessDiscoveryWithoutHandshake(string? token)
    {
        await using var fixture = await Fixture.StartAsync(token);
        using var client = fixture.CreateClient(token);
        using var discover = await SendAsync(client, """{"jsonrpc":"2.0","id":1,"method":"server/discover","params":{}}""", "2026-07-28");
        Assert.AreEqual(HttpStatusCode.OK, discover.StatusCode);
        Assert.IsFalse(discover.Headers.Contains("Mcp-Session-Id"));
        var result = await ReadResultAsync(discover);
        Assert.IsTrue(result.GetProperty("capabilities").TryGetProperty("tools", out _));
        Assert.IsTrue(result.GetProperty("supportedVersions").EnumerateArray().Any(version => version.GetString() == "2026-07-28"));
        Assert.AreEqual("ProjectMemory", result.GetProperty("_meta").GetProperty("io.modelcontextprotocol/serverInfo").GetProperty("name").GetString());
        using var list = await SendAsync(client, """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""", "2026-07-28");
        Assert.AreEqual(HttpStatusCode.OK, list.StatusCode);
        Assert.IsFalse(list.Headers.Contains("Mcp-Session-Id"));
        AssertToolDiscovery((await ReadResultAsync(list)).GetProperty("tools"));
    }

    /// <summary>
    /// Verifies that the newer transport refuses a method header that contradicts its request body.
    /// </summary>
    /// <returns>A task that completes when the test has verified that the newer transport refuses a method header that contradicts its request body.</returns>
    [TestMethod]
    public async Task StatelessTransportRejectsMismatchedMethodHeader()
    {
        await using var fixture = await Fixture.StartAsync();
        using var client = fixture.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientInfo":{"name":"contract-test","version":"1"},"io.modelcontextprotocol/clientCapabilities":{}}}}""", Encoding.UTF8, "application/json")
        };

        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
        request.Headers.Add("Mcp-Method", "server/discover");
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Verifies that all MCP transport methods reject missing, invalid or malformed tokens before dispatch.
    /// </summary>
    /// <returns>A task that completes when the test has verified that all MCP transport methods reject missing, invalid or malformed tokens before dispatch.</returns>
    [TestMethod]
    public async Task ConfiguredTokenRejectsUnauthorizedRequests()
    {
        await using var fixture = await Fixture.StartAsync("isolated-test-token");
        using var client = fixture.CreateClient();
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Delete })
        {
            foreach (var header in new string?[] { null, "Bearer wrong", "Basic isolated-test-token", "Bearer", "Bearer isolated-test-token, Bearer isolated-test-token" })
            {
                using var request = new HttpRequestMessage(method, "/mcp");
                if (header is not null)
                {
                    request.Headers.TryAddWithoutValidation("Authorization", header);
                }

                using var response = await client.SendAsync(request);
                Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
                Assert.AreEqual("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
                Assert.AreEqual("", await response.Content.ReadAsStringAsync());
            }
        }

        using var health = await client.GetAsync("/health/ready");
        Assert.AreEqual(HttpStatusCode.OK, health.StatusCode);
    }

    /// <summary>
    /// Verifies that readiness reflects storage loss and recovery independently of liveness.
    /// </summary>
    /// <returns>A task that completes when the test has verified that readiness reflects storage loss and recovery independently of liveness.</returns>
    [TestMethod]
    public async Task ReadinessTracksStorageAvailability()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Moving a directory containing an open SQLite database is not supported by Windows file sharing.");
        }

        await using var fixture = await Fixture.StartAsync();
        using var client = fixture.CreateClient();
        using var ready = await client.GetAsync("/health/ready");
        Assert.AreEqual(HttpStatusCode.OK, ready.StatusCode);
        Assert.IsEmpty(Directory.GetFiles(fixture.Root, ".readiness-*"));
        var moved = fixture.Root + "-moved";
        Directory.Move(fixture.Root, moved);
        try
        {
            using var unavailable = await client.GetAsync("/health/ready");
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
            Assert.AreEqual("Unhealthy", await unavailable.Content.ReadAsStringAsync());
            using var live = await client.GetAsync("/health/live");
            Assert.AreEqual(HttpStatusCode.OK, live.StatusCode);
        }
        finally
        {
            Directory.Move(moved, fixture.Root);
        }

        using var restored = await client.GetAsync("/health/ready");
        Assert.AreEqual(HttpStatusCode.OK, restored.StatusCode);
    }

    /// <summary>
    /// Verifies that invalid storage, log-level and token settings fail before a listener opens.
    /// </summary>
    /// <param name="key">The configuration key to override.</param>
    /// <param name="value">The invalid configuration value.</param>
    /// <returns>A task that completes when the test has verified that invalid storage, log-level and token settings fail before a listener opens.</returns>
    [TestMethod]
    [DataRow("ProjectMemory:DataDirectory", "")]
    [DataRow("ProjectMemory:DataDirectory", "relative-path")]
    [DataRow("ProjectMemory:LogLevel", "invalid-level")]
    [DataRow("ProjectMemory:BearerToken", " ")]
    public async Task InvalidConfigurationFailsStartup(string key, string value)
    {
        var root = Fixture.CreateRoot();
        try
        {
            var builder = Fixture.CreateBuilder(root);
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [key] = value });

            await Assert.ThrowsAsync<OptionsValidationException>(() => ServerHost.CreateAsync(builder));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// Verifies that restarting the host preserves the registry and existing files.
    /// </summary>
    /// <returns>A task that completes when the test has verified that restarting the host preserves the registry and existing files.</returns>
    [TestMethod]
    public async Task RestartPreservesData()
    {
        var root = Fixture.CreateRoot();
        try
        {
            await using (var first = await Fixture.StartAsync(root: root, ownsRoot: false))
            {
                await File.WriteAllTextAsync(Path.Combine(root, "sentinel.md"), "persistent data");
            }

            var original = await File.ReadAllBytesAsync(Path.Combine(root, "ProjectMemory.db"));
            await using (var second = await Fixture.StartAsync(root: root, ownsRoot: false))
            {
                Assert.AreEqual("persistent data", await File.ReadAllTextAsync(Path.Combine(root, "sentinel.md")));
            }

            // Compare raw SQLite bytes only when the server has fully stopped.
            CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(Path.Combine(root, "ProjectMemory.db")));
        }
        finally
        {
            ContextOptions.ClearPool(Path.Combine(root, "ProjectMemory.db"));
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// Verifies that port 8000 defaults to local loopback or container interfaces unless a listener is explicitly configured.
    /// </summary>
    /// <param name="container">The container marker supplied by the runtime environment.</param>
    /// <param name="key">An optional explicit listener setting.</param>
    /// <param name="value">The explicit listener value.</param>
    /// <param name="expectedUrls">The expected URLs after host composition.</param>
    /// <returns>A task that completes when the test has verified that port 8000 defaults to local loopback or container interfaces unless a listener is explicitly configured.</returns>
    [TestMethod]
    [DataRow(null, null, null, "http://127.0.0.1:8000")]
    [DataRow("false", null, null, "http://127.0.0.1:8000")]
    [DataRow("true", null, null, "http://0.0.0.0:8000")]
    [DataRow("1", null, null, "http://0.0.0.0:8000")]
    [DataRow("true", "urls", "http://127.0.0.1:9000", "http://127.0.0.1:9000")]
    [DataRow(null, "http_ports", "9000", null)]
    [DataRow("true", "http_ports", "9000", null)]
    [DataRow(null, "https_ports", "9443", null)]
    [DataRow("true", "Kestrel:Endpoints:Private:Url", "http://127.0.0.1:9000", null)]
    public async Task ListenerDefaultsRespectExplicitConfiguration(string? container, string? key, string? value, string? expectedUrls)
    {
        var root = Fixture.CreateRoot();
        try
        {
            var builder = Fixture.CreateBuilder(root);
            builder.Configuration["urls"] = null;
            builder.Configuration["DOTNET_RUNNING_IN_CONTAINER"] = container;
            if (key is not null)
            {
                builder.Configuration[key] = value;
            }

            await using var app = await ServerHost.CreateAsync(builder);
            Assert.AreEqual(expectedUrls, builder.Configuration["urls"]);
            if (key is not null)
            {
                Assert.AreEqual(value, builder.Configuration[key]);
            }
        }
        finally
        {
            ContextOptions.ClearPool(Path.Combine(root, "ProjectMemory.db"));
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// Verifies that the host retains the startup log-level switch and applies the configured level.
    /// </summary>
    /// <returns>A task that completes when the test has verified that the host retains the startup log-level switch and applies the configured level.</returns>
    [TestMethod]
    public async Task HostRetainsStartupLogLevelSwitch()
    {
        var root = Fixture.CreateRoot();
        try
        {
            var builder = Fixture.CreateBuilder(root);
            builder.Configuration["ProjectMemory:LogLevel"] = "Debug";
            var logLevelSwitch = new LoggingLevelSwitch { MinimumLevel = LogEventLevel.Information };

            builder.Services.AddSingleton(logLevelSwitch);

            await using var app = await ServerHost.CreateAsync(builder);
            Assert.AreSame(logLevelSwitch, app.Services.GetRequiredService<LoggingLevelSwitch>());
            Assert.AreEqual(LogEventLevel.Debug, logLevelSwitch.MinimumLevel);
        }
        finally
        {
            ContextOptions.ClearPool(Path.Combine(root, "ProjectMemory.db"));
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// Verifies that nonexistent roots and files used as roots fail without creating storage.
    /// </summary>
    /// <returns>A task that completes when the test has verified that nonexistent roots and files used as roots fail without creating storage.</returns>
    [TestMethod]
    public async Task MissingOrFileDataRootFailsStartup()
    {
        var root = Fixture.CreateRoot();
        try
        {
            var missing = Path.Combine(root, "missing");
            await Assert.ThrowsAsync<OptionsValidationException>(() => ServerHost.CreateAsync(Fixture.CreateBuilder(missing)));
            Assert.IsFalse(Directory.Exists(missing));
            var file = Path.Combine(root, "file");
            await File.WriteAllTextAsync(file, "existing data");
            await Assert.ThrowsAsync<OptionsValidationException>(() => ServerHost.CreateAsync(Fixture.CreateBuilder(file)));
            Assert.AreEqual("existing data", await File.ReadAllTextAsync(file));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    #endregion

    #region Private Methods

    private static void AssertToolDiscovery(JsonElement tools)
    {
        CollectionAssert.AreEquivalent(new[] { "list_projects", "create_project", "archive_project", "reactivate_project", "resolve_context", "list_context_bindings", "bind_context", "unbind_context", "create_memory", "read_memory", "update_memory", "delete_memory", "delete_project", "list_memories", "search_memories" }, tools.EnumerateArray().Select(tool => tool.GetProperty("name").GetString()).ToArray());

        foreach (var tool in tools.EnumerateArray())
        {
            Assert.IsTrue(tool.TryGetProperty("outputSchema", out _));
            Assert.AreEqual(tool.GetProperty("name").GetString() is "unbind_context" or "update_memory" or "delete_memory" or "delete_project", tool.GetProperty("annotations").GetProperty("destructiveHint").GetBoolean());
            var output = tool.GetProperty("outputSchema");
            CollectionAssert.AreEquivalent(new[] { "contract_version", "ok", "data", "error" }, output.GetProperty("properties").EnumerateObject().Select(property => property.Name).ToArray());
            CollectionAssert.AreEquivalent(new[] { "contract_version", "ok", "data", "error" }, output.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ToArray());
            var expectedDataFields = tool.GetProperty("name").GetString() switch
            {
                "list_projects" => new[] { "projects", "offset", "limit", "has_more" },
                "create_project" => new[] { "project" },
                "archive_project" or "reactivate_project" => new[] { "project", "changed" },
                "resolve_context" => new[] { "context_id", "project_id", "project_name" },
                "list_context_bindings" => new[] { "bindings", "offset", "limit", "has_more" },
                "bind_context" => new[] { "context_id", "project_id", "changed" },
                "create_memory" or "update_memory" or "delete_memory" => new[] { "path", "content_hash" },
                "read_memory" => new[] { "path", "content_hash", "content" },
                "list_memories" => new[] { "paths", "offset", "limit", "has_more" },
                "search_memories" => new[] { "view", "matches", "files", "counts", "context", "offset", "limit", "has_more", "limited_by_request" },
                "delete_project" => new[] { "project_id" },
                _ => new[] { "context_id", "changed" }
            };

            var data = output.GetProperty("properties").GetProperty("data");
            CollectionAssert.AreEquivalent(expectedDataFields, data.GetProperty("properties").EnumerateObject().Select(property => property.Name).ToArray());
            Assert.IsFalse(output.GetProperty("additionalProperties").GetBoolean());
            Assert.IsFalse(data.GetProperty("additionalProperties").GetBoolean());
            Assert.IsFalse(output.GetProperty("properties").GetProperty("error").GetProperty("additionalProperties").GetBoolean());
            var input = tool.GetProperty("inputSchema");
            var required = tool.GetProperty("name").GetString() switch
            {
                "create_memory" => new[] { "context_id", "path", "content" },
                "read_memory" => new[] { "context_id", "path" },
                "list_memories" => new[] { "context_id" },
                "search_memories" => new[] { "context_id", "query" },
                "update_memory" => new[] { "context_id", "path", "content", "expected_content_hash" },
                "delete_memory" => new[] { "context_id", "path", "expected_content_hash" },
                "delete_project" => new[] { "project_id" },
                _ => null
            };
            if (required is not null)
            {
                CollectionAssert.AreEquivalent(required, input.GetProperty("required").EnumerateArray().Select(field => field.GetString()).ToArray());
            }

            if (tool.GetProperty("name").GetString() is "list_memories" or "search_memories")
            {
                var annotations = tool.GetProperty("annotations");
                Assert.IsTrue(annotations.GetProperty("readOnlyHint").GetBoolean());
                Assert.IsTrue(annotations.GetProperty("idempotentHint").GetBoolean());
                Assert.IsFalse(annotations.GetProperty("openWorldHint").GetBoolean());
                CollectionAssert.AreEquivalent(required!.Concat(tool.GetProperty("name").GetString() == "list_memories" ? new[] { "folder", "offset", "limit" } : ["folder", "file", "match", "files", "output"]).ToArray(),
                    input.GetProperty("properties").EnumerateObject().Select(property => property.Name).ToArray());
                Assert.AreEqual("1.0", output.GetProperty("properties").GetProperty("contract_version").GetProperty("const").GetString());
                if (data.GetProperty("properties").TryGetProperty("matches", out var matches))
                {
                    var hit = matches.GetProperty("items");
                    Assert.IsFalse(hit.GetProperty("additionalProperties").GetBoolean());
                    CollectionAssert.AreEquivalent(new[] { "path", "line", "end_line", "excerpt", "excerpt_truncated", "submatches" },
                        hit.GetProperty("required").EnumerateArray().Select(field => field.GetString()).ToArray());
                }
            }

            Assert.IsFalse(input.GetProperty("additionalProperties").GetBoolean());
            Assert.IsFalse(input.GetProperty("properties").TryGetProperty("cancellationToken", out _));
            Assert.IsFalse(input.GetProperty("properties").TryGetProperty("projectManager", out _));
        }
    }

    #endregion

    #region Test Classes

    #endregion
}
