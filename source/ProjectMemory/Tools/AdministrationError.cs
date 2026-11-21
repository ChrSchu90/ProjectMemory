namespace ProjectMemory.Tools;

/// <summary>
/// Carries a stable error code and a safe explanation without infrastructure details.
/// </summary>
/// <param name="Code">The versioned application error code.</param>
/// <param name="Message">The client-facing explanation.</param>
internal sealed record AdministrationError(string Code, string Message);
