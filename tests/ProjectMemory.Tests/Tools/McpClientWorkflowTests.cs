namespace ProjectMemory.Tests.Tools;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ProjectMemory.Database;
using ProjectMemory.Tests.Hosting;

/// <summary>
/// Exercises a complete memory workflow through the official MCP client against an isolated live server.
/// </summary>
[TestClass]
public sealed class McpClientWorkflowTests : ServerHostTestBase
{
    #region Static Fields

    private const string FirstContext = "chat:ClientWorkflow";
    private const string SecondContext = "chat:OtherWorkflow";
    private const string MemoryPath = "Notes/session.md";

    #endregion

    #region Private Fields

    #endregion

    #region Test Constructors

    #endregion

    #region Test Preparation

    #endregion

    #region Tests

    /// <summary>
    /// Verifies that a real MCP SDK client discovers tools and completes isolated Markdown CRUD and PCRE2 search through both supported protocol revisions.
    /// </summary>
    /// <param name="protocolVersion">The MCP revision to negotiate with the test server.</param>
    /// <param name="token">The optional bearer token used by the test server and client.</param>
    /// <returns>A task completing after MCP results and the actual filesystem state have been verified.</returns>
    [TestMethod]
    [DataRow("2025-11-25", null)]
    [DataRow("2026-07-28", "isolated-client-token")]
    public async Task ClientWorkflowPersistsAndSearchesIsolatedMemories(string protocolVersion, string? token)
    {
        await using var host = await Fixture.StartAsync(token);
        using var endpointClient = host.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var cancellationToken = timeout.Token;
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(endpointClient.BaseAddress!, "mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            EnableStandaloneGetStream = false,
            AdditionalHeaders = token is null ? null : new Dictionary<string, string> { ["Authorization"] = "Bearer " + token }
        });
        await using var client = await McpClient.CreateAsync(transport, new McpClientOptions { ProtocolVersion = protocolVersion }, cancellationToken: cancellationToken);

        var names = (await client.ListToolsAsync(cancellationToken: cancellationToken)).Select(tool => tool.Name).ToArray();
        CollectionAssert.AreEquivalent(new[]
        {
            "archive_project", "bind_context", "create_memory", "create_project", "delete_memory", "delete_project",
            "list_context_bindings", "list_memories", "list_projects", "reactivate_project", "read_memory",
            "resolve_context", "search_memories", "unbind_context", "update_memory"
        }, names);

        var first = await CallAsync(client, "create_project", new() { ["project_name"] = "Client workflow A" }, cancellationToken);
        var second = await CallAsync(client, "create_project", new() { ["project_name"] = "Client workflow B" }, cancellationToken);
        var firstId = first.GetProperty("data").GetProperty("project").GetProperty("project_id").GetString()!;
        var secondId = second.GetProperty("data").GetProperty("project").GetProperty("project_id").GetString()!;
        Assert.AreNotEqual(firstId, secondId);
        Assert.AreNotEqual(Guid.Empty, Guid.Parse(firstId));
        Assert.AreNotEqual(Guid.Empty, Guid.Parse(secondId));

        await CallAsync(client, "bind_context", new() { ["context_id"] = FirstContext, ["project_id"] = firstId }, cancellationToken);
        await CallAsync(client, "bind_context", new() { ["context_id"] = SecondContext, ["project_id"] = secondId }, cancellationToken);

        const string content = "# Session\nissue: PM-42\nKeep this note.\n";
        const string otherContent = "# Other\nissue: PM-42\nDifferent project.\n";
        var file = Path.Combine(host.Root, "projects", firstId, "Notes", "session.md");
        var otherFile = Path.Combine(host.Root, "projects", secondId, "Notes", "other.md");
        var created = await CallAsync(client, "create_memory", new()
        {
            ["context_id"] = FirstContext, ["path"] = MemoryPath, ["content"] = content
        }, cancellationToken);
        await CallAsync(client, "create_memory", new()
        {
            ["context_id"] = SecondContext, ["path"] = "Notes/other.md", ["content"] = otherContent
        }, cancellationToken);
        var hash = created.GetProperty("data").GetProperty("content_hash").GetString()!;
        Assert.AreEqual(Hash(content), hash);
        Assert.AreEqual(content, await File.ReadAllTextAsync(file, cancellationToken));
        Assert.AreEqual(otherContent, await File.ReadAllTextAsync(otherFile, cancellationToken));

        var read = await CallAsync(client, "read_memory", new() { ["context_id"] = FirstContext, ["path"] = MemoryPath }, cancellationToken);
        Assert.AreEqual(content, read.GetProperty("data").GetProperty("content").GetString());
        Assert.AreEqual(hash, read.GetProperty("data").GetProperty("content_hash").GetString());
        var listed = await CallAsync(client, "list_memories", new() { ["context_id"] = FirstContext }, cancellationToken);
        var paths = listed.GetProperty("data").GetProperty("paths");
        Assert.AreEqual(1, paths.GetArrayLength());
        Assert.AreEqual(MemoryPath, paths[0].GetString());

        var search = new Dictionary<string, object?>
        {
            ["context_id"] = FirstContext,
            ["query"] = "(?<=issue: )PM-42",
            ["match"] = new { syntax = "regex", engine = "pcre2" }
        };
        var found = await CallAsync(client, "search_memories", search, cancellationToken);
        var matches = found.GetProperty("data").GetProperty("matches");
        Assert.AreEqual(1, matches.GetArrayLength());
        Assert.AreEqual(MemoryPath, matches[0].GetProperty("path").GetString());
        Assert.AreEqual(2, matches[0].GetProperty("line").GetInt32());
        Assert.AreEqual("issue: PM-42", matches[0].GetProperty("excerpt").GetString());
        await CallAsync(client, "read_memory", new() { ["context_id"] = "chat:clientworkflow", ["path"] = MemoryPath }, cancellationToken, "CONTEXT_UNBOUND");

        await CallAsync(client, "update_memory", new()
        {
            ["context_id"] = FirstContext, ["path"] = MemoryPath, ["content"] = "Wrong write", ["expected_content_hash"] = new string('0', 64)
        }, cancellationToken, "CONFLICT");
        Assert.AreEqual(content, await File.ReadAllTextAsync(file, cancellationToken));

        const string replacement = "# Session\nResolved issue.\n";
        var updated = await CallAsync(client, "update_memory", new()
        {
            ["context_id"] = FirstContext, ["path"] = MemoryPath, ["content"] = replacement, ["expected_content_hash"] = hash
        }, cancellationToken);
        var newHash = updated.GetProperty("data").GetProperty("content_hash").GetString()!;
        Assert.AreEqual(Hash(replacement), newHash);
        Assert.AreEqual(replacement, await File.ReadAllTextAsync(file, cancellationToken));
        found = await CallAsync(client, "search_memories", search, cancellationToken);
        Assert.AreEqual(0, found.GetProperty("data").GetProperty("matches").GetArrayLength());

        await CallAsync(client, "delete_memory", new()
        {
            ["context_id"] = FirstContext, ["path"] = MemoryPath, ["expected_content_hash"] = newHash
        }, cancellationToken);
        Assert.IsFalse(File.Exists(file));
        Assert.IsTrue(File.Exists(otherFile));
        Assert.AreEqual(otherContent, await File.ReadAllTextAsync(otherFile, cancellationToken));
        var otherSearch = await CallAsync(client, "search_memories", new()
        {
            ["context_id"] = SecondContext, ["query"] = "PM-42"
        }, cancellationToken);
        Assert.AreEqual("Notes/other.md", otherSearch.GetProperty("data").GetProperty("matches")[0].GetProperty("path").GetString());
    }

    /// <summary>
    /// Verifies that competing updates from two independent MCP clients commit exactly one version and leave both readers with the same file state.
    /// </summary>
    /// <returns>A task completing after the CAS race, the structured conflict, and both clients' reads have been verified.</returns>
    [TestMethod]
    public async Task IndependentClientsObserveOneCommittedCasUpdate()
    {
        await using var host = await Fixture.StartAsync();
        using var endpoint = host.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var cancellationToken = timeout.Token;
        await using var firstTransport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(endpoint.BaseAddress!, "mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            EnableStandaloneGetStream = false
        });
        await using var secondTransport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(endpoint.BaseAddress!, "mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            EnableStandaloneGetStream = false
        });
        await using var firstClient = await McpClient.CreateAsync(firstTransport, new McpClientOptions { ProtocolVersion = "2025-11-25" }, cancellationToken: cancellationToken);
        await using var secondClient = await McpClient.CreateAsync(secondTransport, new McpClientOptions { ProtocolVersion = "2025-11-25" }, cancellationToken: cancellationToken);

        const string contextId = "chat:ConcurrentClients";
        const string original = "# Shared note\nVersion zero.\n";
        const string firstUpdate = "# Shared note\nWritten by first client.\n";
        const string secondUpdate = "# Shared note\nWritten by second client.\n";
        var project = await CallAsync(firstClient, "create_project", new() { ["project_name"] = "Concurrent MCP clients" }, cancellationToken);
        var projectId = project.GetProperty("data").GetProperty("project").GetProperty("project_id").GetString()!;
        await CallAsync(firstClient, "bind_context", new() { ["context_id"] = contextId, ["project_id"] = projectId }, cancellationToken);
        var created = await CallAsync(firstClient, "create_memory", new()
        {
            ["context_id"] = contextId, ["path"] = MemoryPath, ["content"] = original
        }, cancellationToken);
        var originalHash = created.GetProperty("data").GetProperty("content_hash").GetString()!;
        var file = Path.Combine(host.Root, "projects", projectId, "Notes", "session.md");

        var firstRead = await CallAsync(firstClient, "read_memory", new() { ["context_id"] = contextId, ["path"] = MemoryPath }, cancellationToken);
        var secondRead = await CallAsync(secondClient, "read_memory", new() { ["context_id"] = contextId, ["path"] = MemoryPath }, cancellationToken);
        Assert.AreEqual(originalHash, firstRead.GetProperty("data").GetProperty("content_hash").GetString());
        Assert.AreEqual(originalHash, secondRead.GetProperty("data").GetProperty("content_hash").GetString());
        Assert.AreEqual(original, secondRead.GetProperty("data").GetProperty("content").GetString());

        var firstWrite = firstClient.CallToolAsync("update_memory", new Dictionary<string, object?>
        {
            ["context_id"] = contextId, ["path"] = MemoryPath, ["content"] = firstUpdate, ["expected_content_hash"] = originalHash
        }, cancellationToken: cancellationToken).AsTask();
        var secondWrite = secondClient.CallToolAsync("update_memory", new Dictionary<string, object?>
        {
            ["context_id"] = contextId, ["path"] = MemoryPath, ["content"] = secondUpdate, ["expected_content_hash"] = originalHash
        }, cancellationToken: cancellationToken).AsTask();
        var results = await Task.WhenAll(firstWrite, secondWrite);
        Assert.AreNotEqual(results[0].IsError is true, results[1].IsError is true, "Exactly one CAS update must succeed.");
        var firstResult = VerifyResult(results[0], "update_memory", results[0].IsError is true ? "CONFLICT" : null);
        var secondResult = VerifyResult(results[1], "update_memory", results[1].IsError is true ? "CONFLICT" : null);
        var winningContent = results[0].IsError is true ? secondUpdate : firstUpdate;
        var winningResult = results[0].IsError is true ? secondResult : firstResult;
        var winningHash = Hash(winningContent);
        Assert.AreEqual(winningHash, winningResult.GetProperty("data").GetProperty("content_hash").GetString());
        Assert.AreEqual(winningContent, await File.ReadAllTextAsync(file, cancellationToken));

        foreach (var client in new[] { firstClient, secondClient })
        {
            var read = await CallAsync(client, "read_memory", new() { ["context_id"] = contextId, ["path"] = MemoryPath }, cancellationToken);
            Assert.AreEqual(winningContent, read.GetProperty("data").GetProperty("content").GetString());
            Assert.AreEqual(winningHash, read.GetProperty("data").GetProperty("content_hash").GetString());
        }
    }

    /// <summary>
    /// Verifies that a new MCP SDK connection can resolve, search, and update a stored memory after the server is fully restarted.
    /// </summary>
    /// <returns>A task completing after persistent routing and Markdown operations have been verified across two server lifetimes.</returns>
    [TestMethod]
    public async Task NewClientCanResumeMemoryWorkflowAfterServerRestart()
    {
        const string token = "restart-client-token";
        const string contextId = "local:ClientRestart";
        const string content = "# Session\nissue: PM-903\nPreserved across restart.\n";
        const string replacement = "# Session\nissue: PM-903\nUpdated after restart.\n";
        var root = Fixture.CreateRoot();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var cancellationToken = timeout.Token;
        try
        {
            string projectId;
            string originalHash;
            await using (var firstHost = await Fixture.StartAsync(token: token, root: root, ownsRoot: false))
            {
                using var endpoint = firstHost.CreateClient();
                await using var transport = new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = new Uri(endpoint.BaseAddress!, "mcp"),
                    TransportMode = HttpTransportMode.StreamableHttp,
                    EnableStandaloneGetStream = false,
                    AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token }
                });
                await using var client = await McpClient.CreateAsync(transport, new McpClientOptions { ProtocolVersion = "2026-07-28" }, cancellationToken: cancellationToken);

                var project = await CallAsync(client, "create_project", new() { ["project_name"] = "MCP restart workflow" }, cancellationToken);
                projectId = project.GetProperty("data").GetProperty("project").GetProperty("project_id").GetString()!;
                await CallAsync(client, "bind_context", new() { ["context_id"] = contextId, ["project_id"] = projectId }, cancellationToken);
                var created = await CallAsync(client, "create_memory", new()
                {
                    ["context_id"] = contextId, ["path"] = MemoryPath, ["content"] = content
                }, cancellationToken);
                originalHash = created.GetProperty("data").GetProperty("content_hash").GetString()!;
                Assert.AreEqual(Hash(content), originalHash);
            }

            var file = Path.Combine(root, "projects", projectId, "Notes", "session.md");
            Assert.AreEqual(content, await File.ReadAllTextAsync(file, cancellationToken));
            await using (var secondHost = await Fixture.StartAsync(token: token, root: root, ownsRoot: false))
            {
                using var endpoint = secondHost.CreateClient();
                await using var transport = new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = new Uri(endpoint.BaseAddress!, "mcp"),
                    TransportMode = HttpTransportMode.StreamableHttp,
                    EnableStandaloneGetStream = false,
                    AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token }
                });
                await using var client = await McpClient.CreateAsync(transport, new McpClientOptions { ProtocolVersion = "2026-07-28" }, cancellationToken: cancellationToken);

                var resolved = await CallAsync(client, "resolve_context", new() { ["context_id"] = contextId }, cancellationToken);
                Assert.AreEqual(projectId, resolved.GetProperty("data").GetProperty("project_id").GetString());
                var read = await CallAsync(client, "read_memory", new() { ["context_id"] = contextId, ["path"] = MemoryPath }, cancellationToken);
                Assert.AreEqual(content, read.GetProperty("data").GetProperty("content").GetString());
                Assert.AreEqual(originalHash, read.GetProperty("data").GetProperty("content_hash").GetString());
                var found = await CallAsync(client, "search_memories", new()
                {
                    ["context_id"] = contextId,
                    ["query"] = "(?<=issue: )PM-903",
                    ["match"] = new { syntax = "regex", engine = "pcre2" }
                }, cancellationToken);
                var matches = found.GetProperty("data").GetProperty("matches");
                Assert.AreEqual(1, matches.GetArrayLength());
                Assert.AreEqual(MemoryPath, matches[0].GetProperty("path").GetString());

                var updated = await CallAsync(client, "update_memory", new()
                {
                    ["context_id"] = contextId, ["path"] = MemoryPath, ["content"] = replacement, ["expected_content_hash"] = originalHash
                }, cancellationToken);
                Assert.AreEqual(Hash(replacement), updated.GetProperty("data").GetProperty("content_hash").GetString());
                Assert.AreEqual(replacement, await File.ReadAllTextAsync(file, cancellationToken));
                var afterUpdate = await CallAsync(client, "read_memory", new() { ["context_id"] = contextId, ["path"] = MemoryPath }, cancellationToken);
                Assert.AreEqual(replacement, afterUpdate.GetProperty("data").GetProperty("content").GetString());
                Assert.AreEqual(Hash(replacement), afterUpdate.GetProperty("data").GetProperty("content_hash").GetString());
            }
        }
        finally
        {
            ContextOptions.ClearPool(Path.Combine(root, "ProjectMemory.db"));
            Directory.Delete(root, true);
        }
    }

    #endregion

    #region Private Methods

    private static async Task<JsonElement> CallAsync(McpClient client, string name, Dictionary<string, object?> arguments, CancellationToken cancellationToken, string? expectedError = null)
    {
        var result = await client.CallToolAsync(name, arguments, cancellationToken: cancellationToken);
        return VerifyResult(result, name, expectedError);
    }

    private static JsonElement VerifyResult(CallToolResult result, string name, string? expectedError = null)
    {
        Assert.AreEqual(expectedError is not null, result.IsError is true, name);
        Assert.IsTrue(result.StructuredContent.HasValue, name);
        var envelope = result.StructuredContent.Value;
        Assert.AreEqual("1.0", envelope.GetProperty("contract_version").GetString());
        Assert.AreEqual(expectedError is null, envelope.GetProperty("ok").GetBoolean(), name);
        if (expectedError is null)
        {
            Assert.AreEqual(JsonValueKind.Null, envelope.GetProperty("error").ValueKind);
        }
        else
        {
            Assert.AreEqual(expectedError, envelope.GetProperty("error").GetProperty("code").GetString());
            Assert.AreEqual(JsonValueKind.Null, envelope.GetProperty("data").ValueKind);
        }

        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        using var fallback = JsonDocument.Parse(text);
        Assert.IsTrue(JsonElement.DeepEquals(envelope, fallback.RootElement), name);
        return envelope.Clone();
    }

    private static string Hash(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    #endregion

    #region Test Classes

    #endregion
}
