namespace ProjectMemory.Tests.Hosting;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjectMemory.Hosting;
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
        using var call = await SendAsync(client, """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"search_memories","arguments":{"context_id":"chat:test"}}}""");
        var error = await ReadMessageAsync(call);
        Assert.AreEqual(3, error.GetProperty("id").GetInt32());
        Assert.IsFalse(error.TryGetProperty("result", out _));
        Assert.IsLessThan(0, error.GetProperty("error").GetProperty("code").GetInt32());
        Assert.IsFalse(string.IsNullOrEmpty(error.GetProperty("error").GetProperty("message").GetString()));
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
                CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(Path.Combine(root, "ProjectMemory.db")));
                Assert.AreEqual("persistent data", await File.ReadAllTextAsync(Path.Combine(root, "sentinel.md")));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
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
            SqliteConnection.ClearAllPools();
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
            SqliteConnection.ClearAllPools();
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

    /// <summary>
    /// Verifies that startup, requests and storage failures at Debug level disclose neither the configured token nor host paths.
    /// </summary>
    /// <returns>A task that completes when the test has verified that startup, requests and storage failures at Debug level disclose neither the configured token nor host paths.</returns>
    [TestMethod]
    [DoNotParallelize]
    public async Task DiagnosticLogsAndResponsesExcludeSecretsAndHostPaths()
    {
        var output = Console.Out;
        using var captured = new StringWriter();
        const string token = "isolated-log-test-secret";
        string root;
        Console.SetOut(captured);
        try
        {
            await using var fixture = await Fixture.StartAsync(token, logLevel: "Debug");
            root = fixture.Root;
            using var client = fixture.CreateClient();
            using var denied = await client.PostAsync("/mcp", new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
            Assert.AreEqual("", await denied.Content.ReadAsStringAsync());
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var list = await SendAsync(client, """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""");
            Assert.AreEqual(HttpStatusCode.OK, list.StatusCode);
            var body = await list.Content.ReadAsStringAsync();
            Assert.IsFalse(body.Contains(token, StringComparison.Ordinal));
            Assert.IsFalse(body.Contains(root, StringComparison.Ordinal));
            var moved = root + "-moved";
            Directory.Move(root, moved);
            try
            {
                using var unavailable = await client.GetAsync("/health/ready");
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
                Assert.AreEqual("Unhealthy", await unavailable.Content.ReadAsStringAsync());
            }
            finally
            {
                Directory.Move(moved, root);
            }
        }
        finally
        {
            Console.SetOut(output);
        }

        var logs = captured.ToString();
        Assert.IsTrue(logs.Contains("Creating lifecycle database.", StringComparison.Ordinal));
        Assert.IsFalse(logs.Contains(token, StringComparison.Ordinal), "Logs disclosed the configured token.");
        Assert.IsFalse(logs.Contains(root, StringComparison.Ordinal), "Logs disclosed the data root.");
        Assert.IsFalse(logs.Contains(Environment.CurrentDirectory, StringComparison.Ordinal), "Logs disclosed the content root.");
    }

    #endregion

    #region Private Methods

    private static void AssertToolDiscovery(JsonElement tools)
    {
        CollectionAssert.AreEquivalent(new[] { "list_projects", "create_project", "archive_project", "reactivate_project", "resolve_context", "list_context_bindings", "bind_context", "unbind_context", "create_memory", "read_memory", "update_memory", "delete_memory", "delete_project" }, tools.EnumerateArray().Select(tool => tool.GetProperty("name").GetString()).ToArray());

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
                "update_memory" => new[] { "context_id", "path", "content", "expected_content_hash" },
                "delete_memory" => new[] { "context_id", "path", "expected_content_hash" },
                "delete_project" => new[] { "project_id" },
                _ => null
            };
            if (required is not null)
            {
                CollectionAssert.AreEquivalent(required, input.GetProperty("required").EnumerateArray().Select(field => field.GetString()).ToArray());
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
