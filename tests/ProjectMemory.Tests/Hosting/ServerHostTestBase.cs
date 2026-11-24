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
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjectMemory.Configuration;
using ProjectMemory.Database;
using ProjectMemory.Hosting;

/// <summary>
/// Shares isolated host fixtures and MCP transport helpers without declaring test cases or shared mutable test state.
/// </summary>
public abstract class ServerHostTestBase
{
    #region Static Fields

    #endregion

    #region Private Fields

    #endregion

    #region Constructors

    #endregion

    #region Properties

    #endregion

    #region Public Methods

    /// <summary>
    /// Sends an MCP request with the headers and metadata required by the selected protocol revision.
    /// </summary>
    /// <param name="client">The caller-owned client targeting an isolated test host.</param>
    /// <param name="body">The JSON-RPC request body to send to the MCP endpoint.</param>
    /// <param name="protocolVersion">The MCP protocol revision controlling request headers and metadata.</param>
    /// <returns>A task containing the HTTP response that the caller must dispose.</returns>
    protected static async Task<HttpResponseMessage> SendAsync(HttpClient client, string body, string protocolVersion = "2025-11-25")
    {
        if (protocolVersion == "2026-07-28")
        {
            var message = JsonNode.Parse(body)!;
            message["params"]!["_meta"] = JsonNode.Parse("""{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientInfo":{"name":"contract-test","version":"1"},"io.modelcontextprotocol/clientCapabilities":{}}""");
            body = message.ToJsonString();
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.Add("MCP-Protocol-Version", protocolVersion);
        using var json = JsonDocument.Parse(body);
        request.Headers.Add("Mcp-Method", json.RootElement.GetProperty("method").GetString());
        if (json.RootElement.GetProperty("method").GetString() == "tools/call")
        {
            request.Headers.Add("Mcp-Name", json.RootElement.GetProperty("params").GetProperty("name").GetString());
        }

        return await client.SendAsync(request);
    }

    /// <summary>
    /// Reads the result object from a JSON-RPC response returned by a test host.
    /// </summary>
    /// <param name="response">The caller-owned response containing JSON or server-sent events.</param>
    /// <returns>A task containing a detached JSON result element.</returns>
    protected static async Task<JsonElement> ReadResultAsync(HttpResponseMessage response)
    {
        return (await ReadMessageAsync(response)).GetProperty("result");
    }

    /// <summary>
    /// Reads a JSON-RPC message from JSON or server-sent event content.
    /// </summary>
    /// <param name="response">The caller-owned HTTP response to read.</param>
    /// <returns>A task containing a detached JSON message that survives disposal of the parsing document.</returns>
    protected static async Task<JsonElement> ReadMessageAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            body = body.Split('\n').First(l => l.StartsWith("data: ", StringComparison.Ordinal))[6..];
        }

        using var json = JsonDocument.Parse(body);
        return json.RootElement.Clone();
    }

    /// <summary>
    /// Sends a tool invocation so the caller can inspect the HTTP response.
    /// </summary>
    /// <param name="client">The client targeting the isolated MCP host.</param>
    /// <param name="name">The published tool name.</param>
    /// <param name="arguments">The arguments serialized into the tool call.</param>
    /// <param name="protocol">The MCP protocol revision used for the request.</param>
    /// <returns>A task containing the HTTP response that the caller must dispose.</returns>
    protected static Task<HttpResponseMessage> SendToolAsync(HttpClient client, string name, object arguments, string protocol)
    {
        var body = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 21, method = "tools/call", @params = new { name, arguments } });
        return SendAsync(client, body, protocol);
    }

    /// <summary>
    /// Calls the named tool and verifies both encodings of its expected envelope.
    /// </summary>
    /// <param name="client">The isolated MCP client.</param>
    /// <param name="name">The published tool name.</param>
    /// <param name="arguments">The flat tool arguments.</param>
    /// <param name="protocol">The requested MCP protocol revision.</param>
    /// <param name="error">The expected error code, or null for success.</param>
    /// <returns>The verified envelope.</returns>
    protected static async Task<JsonElement> CallToolAsync(HttpClient client, string name, object arguments, string protocol = "2025-11-25", string? error = null)
    {
        using var response = await SendToolAsync(client, name, arguments, protocol);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var result = await ReadResultAsync(response);
        var content = result.GetProperty("structuredContent");
        Assert.AreEqual("1.0", content.GetProperty("contract_version").GetString());
        Assert.AreEqual(error is null, content.GetProperty("ok").GetBoolean(), content.GetRawText());
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

    #region Private Methods

    #endregion

    #region Nested Types

    /// <summary>
    /// Owns a local application and optionally its disposable data root.
    /// </summary>
    /// <param name="app">The application owned by the fixture.</param>
    /// <param name="root">The isolated data directory.</param>
    /// <param name="ownsRoot">Whether disposal removes the data directory.</param>
    protected sealed class Fixture(WebApplication app, string root, bool ownsRoot) : IAsyncDisposable
    {
        #region Static Fields

        #endregion

        #region Private Fields

        #endregion

        #region Constructors

        #endregion

        #region Properties

        /// <summary>
        /// Gets the isolated data directory used by this fixture.
        /// </summary>
        public string Root => root;

        /// <summary>
        /// Gets the services of this isolated host for deterministic coordination tests.
        /// </summary>
        internal IServiceProvider Services => app.Services;

        #endregion

        #region Public Methods

        /// <summary>
        /// Creates an isolated temporary data directory owned by the caller.
        /// </summary>
        /// <returns>The absolute path of the new directory.</returns>
        public static string CreateRoot()
        {
            var root = Path.Combine(Path.GetTempPath(), "ProjectMemory.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        /// <summary>
        /// Creates an application builder with isolated configuration and a loopback listener.
        /// </summary>
        /// <param name="root">The isolated data directory.</param>
        /// <param name="token">The optional server bearer token.</param>
        /// <param name="logLevel">The optional diagnostic level for this fixture.</param>
        /// <returns>The configured application builder.</returns>
        public static WebApplicationBuilder CreateBuilder(string root, string? token = null, string? logLevel = null)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production", Args = [] });

            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["urls"] = "http://127.0.0.1:0",
                ["ProjectMemory:DataDirectory"] = root,
                ["ProjectMemory:BearerToken"] = token,
                ["ProjectMemory:LogLevel"] = logLevel ?? ProjectMemoryOptions.DefaultLogLevel
            });

            return builder;
        }

        /// <summary>
        /// Starts an application with isolated storage and configuration.
        /// </summary>
        /// <param name="token">The optional server bearer token.</param>
        /// <param name="root">An existing isolated data directory, or null to create one.</param>
        /// <param name="ownsRoot">Whether disposal removes the data directory.</param>
        /// <param name="logLevel">The optional diagnostic level for this fixture.</param>
        /// <param name="configure">Optional services for deterministic fault injection.</param>
        /// <returns>The fixture owning the running application.</returns>
        public static async Task<Fixture> StartAsync(string? token = null, string? root = null, bool ownsRoot = true, string? logLevel = null, Action<IServiceCollection>? configure = null)
        {
            root ??= CreateRoot();
            WebApplication? app = null;
            try
            {
                var builder = CreateBuilder(root, token, logLevel);
                configure?.Invoke(builder.Services);
                app = await ServerHost.CreateAsync(builder);
                await app.StartAsync();
                return new Fixture(app, root, ownsRoot);
            }
            catch
            {
                if (app is not null)
                {
                    await app.DisposeAsync();
                }

                // A failed host startup can leave SQLite connections in its process pool.
                ContextOptions.ClearPool(Path.Combine(root, "ProjectMemory.db"));
                if (ownsRoot)
                {
                    Directory.Delete(root, true);
                }

                throw;
            }
        }

        /// <summary>
        /// Creates an HTTP client owned by the caller for this fixture's listener.
        /// </summary>
        /// <param name="token">The optional bearer token sent by the client.</param>
        /// <returns>The client targeting the local listener.</returns>
        public HttpClient CreateClient(string? token = null)
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(15) };

            if (!string.IsNullOrEmpty(token))
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            return client;
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
            ContextOptions.ClearPool(Path.Combine(root, "ProjectMemory.db"));
            if (ownsRoot)
            {
                Directory.Delete(root, true);
            }
        }

        #endregion

        #region Private Methods

        #endregion
    }

    #endregion
}
