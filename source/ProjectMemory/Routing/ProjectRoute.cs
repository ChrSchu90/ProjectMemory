namespace ProjectMemory.Routing;

using System;

/// <summary>
/// Represents the persisted routing state for a context.
/// </summary>
/// <param name="MemoryProjectId">The immutable identifier of the project owning the context binding.</param>
/// <param name="MemoryProjectName">The human-readable name of the bound project.</param>
/// <param name="ProjectRoutingStatus">The project lifecycle status used to reject archived-project access.</param>
/// <param name="BindingStatus">The binding status used to reject preserved inactive bindings.</param>
public sealed record ProjectRoute(Guid MemoryProjectId, string MemoryProjectName, ProjectStatus ProjectRoutingStatus, Status BindingStatus);
