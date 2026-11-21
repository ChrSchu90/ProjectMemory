namespace ProjectMemory.Routing;

using System;

/// <summary>
/// Represents a registered server-owned memory project.
/// </summary>
/// <param name="MemoryProjectId">The immutable server-owned project identifier.</param>
/// <param name="MemoryProjectName">The human-readable project name.</param>
/// <param name="Status">The routing status.</param>
public sealed record ProjectRouting(Guid MemoryProjectId, string MemoryProjectName, Status Status);
