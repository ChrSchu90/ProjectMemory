namespace ProjectMemory.Tools;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ProjectMemory.Project;
using ProjectMemory.Routing;

/// <summary>
/// Exposes project and context administration operations as attributed MCP methods.
/// </summary>
/// <param name="projectManager">The project manager owning validated registry operations.</param>
/// <param name="logger">The logger used to record storage failure types without infrastructure details.</param>
[McpServerToolType]
internal sealed class AdministrationTools(ProjectManager projectManager, ILogger<AdministrationTools> logger)
{
    #region Static Fields

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerOptions.Default)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    #endregion

    #region Private Fields

    #endregion

    #region Constructors

    #endregion

    #region Properties

    /// <summary>
    /// Gets a host-owned copy of the serializer settings used for schemas and responses.
    /// </summary>
    internal static JsonSerializerOptions SerializerOptions => new(JsonOptions);

    #endregion

    #region Public Methods

    /// <summary>
    /// Lists registered projects without selecting an implicit default project.
    /// </summary>
    /// <param name="offset">The non-negative number of projects to skip.</param>
    /// <param name="limit">The requested page size, from 1 through 200.</param>
    /// <param name="cancellationToken">The token used to cancel the registry query.</param>
    /// <returns>A task containing the project page or a structured error, with matching JSON text.</returns>
    [McpServerTool(Name = "list_projects", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(AdministrationResponse<ProjectListResult>))]
    [Description("List registered active and archived projects without selecting a default project.")]
    public Task<CallToolResult> ListProjectsAsync(
        [Description("Number of projects to skip."), Range(0, int.MaxValue)] int offset = 0,
        [Description("Page size from 1 through 200."), Range(1, 200)] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(() => projectManager.ListProjectsAsync(offset, limit, cancellationToken));
    }

    /// <summary>
    /// Creates an active project with a server-issued immutable identifier.
    /// </summary>
    /// <param name="project_name">The human-readable project name, never a host storage path.</param>
    /// <param name="cancellationToken">The token used to cancel project creation.</param>
    /// <returns>A task containing the created project or a structured validation or storage error.</returns>
    [McpServerTool(Name = "create_project", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(AdministrationResponse<ProjectResult>))]
    [Description("Create an active project with a server-issued immutable ID. No host path can be supplied.")]
    public Task<CallToolResult> CreateProjectAsync(
        [Description("Project display name, from 1 through 255 characters."), MinLength(1), MaxLength(255)] string project_name,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(() => projectManager.CreateProjectAsync(project_name, cancellationToken));
    }

    /// <summary>
    /// Archives the selected project while preserving its identity and bindings.
    /// </summary>
    /// <param name="project_id">The immutable project UUID in hyphenated form.</param>
    /// <param name="cancellationToken">The token used to cancel the status change.</param>
    /// <returns>A task containing the archived project and change flag, or a structured error.</returns>
    [McpServerTool(Name = "archive_project", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(AdministrationResponse<ProjectStatusResult>))]
    [Description("Preserve the project and bindings but block ordinary context resolution.")]
    public Task<CallToolResult> ArchiveProjectAsync(
        [Description("Immutable project UUID in hyphenated form.")] string project_id,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(() => projectManager.SetProjectStatusAsync(project_id, ProjectStatus.Archived, cancellationToken));
    }

    /// <summary>
    /// Reactivates the selected project without changing its existing bindings.
    /// </summary>
    /// <param name="project_id">The immutable project UUID in hyphenated form.</param>
    /// <param name="cancellationToken">The token used to cancel the status change.</param>
    /// <returns>A task containing the active project and change flag, or a structured error.</returns>
    [McpServerTool(Name = "reactivate_project", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(AdministrationResponse<ProjectStatusResult>))]
    [Description("Reactivate an explicitly selected archived project without changing its bindings.")]
    public Task<CallToolResult> ReactivateProjectAsync(
        [Description("Immutable project UUID in hyphenated form.")] string project_id,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(() => projectManager.SetProjectStatusAsync(project_id, ProjectStatus.Active, cancellationToken));
    }

    /// <summary>
    /// Resolves an exact active context and project pair without normalization or fallback.
    /// </summary>
    /// <param name="context_id">The exact case-sensitive context ID with a supported namespace.</param>
    /// <param name="cancellationToken">The token used to cancel the route lookup.</param>
    /// <returns>A task containing the resolved project identity or a structured resolution error.</returns>
    [McpServerTool(Name = "resolve_context", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(AdministrationResponse<ContextResolutionResult>))]
    [Description("Resolve an exact case-sensitive git:, chat: or local: ID. Git origin derivation and the no-remotes requirement for local: are client responsibilities.")]
    public Task<CallToolResult> ResolveContextAsync(
        [Description("Exact git:, chat: or local: key with 1-255 visible ASCII suffix characters."), RegularExpression("^(git|chat|local):[!-~]{1,255}$")] string context_id,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(() => projectManager.ResolveAsync(context_id, cancellationToken));
    }

    /// <summary>
    /// Lists exact context bindings globally or for an explicitly selected project.
    /// </summary>
    /// <param name="project_id">The project UUID to filter by, or null to include all projects.</param>
    /// <param name="offset">The non-negative number of bindings to skip.</param>
    /// <param name="limit">The requested page size, from 1 through 200.</param>
    /// <param name="cancellationToken">The token used to cancel the binding query.</param>
    /// <returns>A task containing the binding page or a structured error, including archived projects.</returns>
    [McpServerTool(Name = "list_context_bindings", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(AdministrationResponse<ContextBindingListResult>))]
    [Description("List exact context bindings globally or for an explicit project, including archived projects.")]
    public Task<CallToolResult> ListContextBindingsAsync(
        [Description("Optional immutable project UUID; omit to include all projects.")] string? project_id = null,
        [Description("Number of bindings to skip."), Range(0, int.MaxValue)] int offset = 0,
        [Description("Page size from 1 through 200."), Range(1, 200)] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(() => projectManager.ListBindingsAsync(project_id, offset, limit, cancellationToken));
    }

    /// <summary>
    /// Creates or confirms an explicit active binding without changing another owner.
    /// </summary>
    /// <param name="context_id">The exact case-sensitive context ID to bind.</param>
    /// <param name="project_id">The immutable UUID of the active target project.</param>
    /// <param name="cancellationToken">The token used to cancel the binding transaction.</param>
    /// <returns>A task containing the binding and change flag, or a structured validation or conflict error.</returns>
    [McpServerTool(Name = "bind_context", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(AdministrationResponse<ContextBindingResult>))]
    [Description("Explicitly bind an exact context to an active project. Confirm an identical active binding; refuse a different owner. Use unbind then bind to move it.")]
    public Task<CallToolResult> BindContextAsync(
        [Description("Exact git:, chat: or local: context ID."), RegularExpression("^(git|chat|local):[!-~]{1,255}$")] string context_id,
        [Description("Immutable UUID of the active target project.")] string project_id,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(() => projectManager.BindAsync(context_id, project_id, cancellationToken));
    }

    /// <summary>
    /// Removes only the explicitly named binding while preserving projects and other bindings.
    /// </summary>
    /// <param name="context_id">The exact case-sensitive context ID whose binding should be removed.</param>
    /// <param name="cancellationToken">The token used to cancel the binding removal.</param>
    /// <returns>A task confirming binding removal or containing a structured error.</returns>
    [McpServerTool(Name = "unbind_context", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(AdministrationResponse<ContextUnbindingResult>))]
    [Description("Remove only an explicit context binding, preserving its project and other bindings.")]
    public Task<CallToolResult> UnbindContextAsync(
        [Description("Exact git:, chat: or local: context ID."), RegularExpression("^(git|chat|local):[!-~]{1,255}$")] string context_id,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(() => projectManager.UnbindAsync(context_id, cancellationToken));
    }

    /// <summary>
    /// Applies the administration contract's closed arguments and fixed version to generated schemas before serving requests.
    /// </summary>
    /// <param name="tools">The attributed tools registered in the current application's service provider.</param>
    internal static void ConfigureSchemas(IEnumerable<McpServerTool> tools)
    {
        foreach (var tool in tools)
        {
            // SDK method schemas omit additionalProperties; close them once at startup, before concurrent requests.
            var input = JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())!.AsObject();
            input["additionalProperties"] = false;
            if (input["properties"]?["project_id"] is JsonObject projectId)
            {
                projectId["type"] = "string";
                projectId["format"] = "uuid";
            }

            tool.ProtocolTool.InputSchema = JsonSerializer.SerializeToElement(input);
            var output = JsonNode.Parse(tool.ProtocolTool.OutputSchema!.Value.GetRawText())!.AsObject();
            output["properties"]!["contract_version"]!["const"] = "1.0";
            tool.ProtocolTool.OutputSchema = JsonSerializer.SerializeToElement(output);
        }
    }

    /// <summary>
    /// Checks flat tool arguments before SDK binding so malformed calls retain the administration error contract.
    /// </summary>
    /// <param name="next">The SDK handler that binds arguments and invokes the attributed method.</param>
    /// <returns>A handler that rejects missing, unknown and incorrectly typed arguments with a structured error.</returns>
    internal static McpRequestHandler<CallToolRequestParams, CallToolResult> ValidateArguments(McpRequestHandler<CallToolRequestParams, CallToolResult> next)
    {
        return async (request, cancellationToken) =>
        {
            if (request.MatchedPrimitive is not McpServerTool tool)
                return await next(request, cancellationToken).ConfigureAwait(false);

            var schema = tool.ProtocolTool.InputSchema;
            var properties = schema.GetProperty("properties");
            var arguments = request.Params?.Arguments;
            if (arguments is not null)
            {
                foreach (var argument in arguments)
                {
                    if (!properties.TryGetProperty(argument.Key, out var property))
                        return Encode(AdministrationResponse<object>.Failure("INVALID_ARGUMENT"));

                    if (!IsValidArgument(property, argument.Value))
                        return Encode(AdministrationResponse<object>.Failure(argument.Key == "context_id" ? "INVALID_CONTEXT" : "INVALID_ARGUMENT"));
                }
            }

            if (schema.TryGetProperty("required", out var required))
            {
                foreach (var field in required.EnumerateArray())
                {
                    var name = field.GetString()!;
                    if (arguments is null || !arguments.ContainsKey(name))
                        return Encode(AdministrationResponse<object>.Failure(name == "context_id" ? "INVALID_CONTEXT" : "INVALID_ARGUMENT"));
                }
            }

            return await next(request, cancellationToken).ConfigureAwait(false);
        };
    }

    #endregion

    #region Private Methods

    private async Task<CallToolResult> ExecuteAsync<TData>(Func<Task<AdministrationResponse<TData>>> operation) where TData : class
    {
        try
        {
            return Encode(await operation().ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is SqliteException or DbUpdateException or IOException or UnauthorizedAccessException)
        {
            logger.LogError("Registry operation failed ({ErrorType}).", exception.GetType().Name);
            return Encode(AdministrationResponse<TData>.Failure("STORAGE_UNAVAILABLE"));
        }
    }

    private static bool IsValidArgument(JsonElement property, JsonElement value)
    {
        var type = property.GetProperty("type");
        var integer = type.ValueKind == JsonValueKind.String && type.GetString() == "integer";
        return integer ? value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _) : value.ValueKind == JsonValueKind.String;
    }

    private static CallToolResult Encode<TData>(AdministrationResponse<TData> response) where TData : class
    {
        var content = JsonSerializer.SerializeToElement(response, JsonOptions);
        return new CallToolResult { IsError = !response.Ok, StructuredContent = content, Content = [new TextContentBlock { Text = content.GetRawText() }] };
    }

    #endregion
}

/// <summary>
/// Describes a project without exposing host storage paths.
/// </summary>
/// <param name="ProjectId">The immutable server-issued project identifier.</param>
/// <param name="ProjectName">The human-readable project name.</param>
/// <param name="Status">The serialized lifecycle state, active or archived.</param>
internal sealed record ProjectDetails(Guid ProjectId, string ProjectName, string Status);

/// <summary>
/// Contains the project returned by creation.
/// </summary>
/// <param name="Project">The newly registered active project.</param>
internal sealed record ProjectResult(ProjectDetails Project);

/// <summary>
/// Contains a project lifecycle operation's result.
/// </summary>
/// <param name="Project">The project snapshot after archiving or reactivation.</param>
/// <param name="Changed">Whether the operation changed the persisted lifecycle status.</param>
internal sealed record ProjectStatusResult(ProjectDetails Project, bool Changed);

/// <summary>
/// Contains a bounded project page and its pagination metadata.
/// </summary>
/// <param name="Projects">The projects in the requested page, including archived projects.</param>
/// <param name="Offset">The number of projects skipped.</param>
/// <param name="Limit">The maximum requested page size.</param>
/// <param name="HasMore">Whether further projects exist beyond this page.</param>
internal sealed record ProjectListResult(IReadOnlyList<ProjectDetails> Projects, int Offset, int Limit, bool HasMore);

/// <summary>
/// Describes an exact context binding independently of its project's lifecycle.
/// </summary>
/// <param name="ContextId">The complete case-sensitive context key.</param>
/// <param name="ProjectId">The immutable identifier of the project owning the binding.</param>
/// <param name="Active">Whether the binding itself is active, regardless of project archival.</param>
internal sealed record ContextBindingDetails(string ContextId, Guid ProjectId, bool Active);

/// <summary>
/// Contains a bounded binding page and its pagination metadata.
/// </summary>
/// <param name="Bindings">The exact bindings in the requested page.</param>
/// <param name="Offset">The number of bindings skipped.</param>
/// <param name="Limit">The maximum requested page size.</param>
/// <param name="HasMore">Whether further bindings exist beyond this page.</param>
internal sealed record ContextBindingListResult(IReadOnlyList<ContextBindingDetails> Bindings, int Offset, int Limit, bool HasMore);

/// <summary>
/// Confirms an explicit binding to an active project.
/// </summary>
/// <param name="ContextId">The exact context key that was bound or confirmed.</param>
/// <param name="ProjectId">The immutable identifier of the bound project.</param>
/// <param name="Changed">Whether a new binding was persisted.</param>
internal sealed record ContextBindingResult(string ContextId, Guid ProjectId, bool Changed);

/// <summary>
/// Confirms removal of an exact context binding.
/// </summary>
/// <param name="ContextId">The exact context key whose binding was removed.</param>
/// <param name="Changed">Whether the binding was removed successfully.</param>
internal sealed record ContextUnbindingResult(string ContextId, bool Changed);

/// <summary>
/// Identifies the active project resolved from an exact context.
/// </summary>
/// <param name="ContextId">The exact context key used for resolution.</param>
/// <param name="ProjectId">The immutable identifier of the resolved project.</param>
/// <param name="ProjectName">The human-readable name of the resolved project.</param>
internal sealed record ContextResolutionResult(string ContextId, Guid ProjectId, string ProjectName);
