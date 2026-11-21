namespace ProjectMemory.Hosting;

using System;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using ProjectMemory.Configuration;

/// <summary>
/// Enforces an optional shared token on every MCP transport request.
/// </summary>
internal sealed class OptionalBearerMiddleware
{
    #region Static Fields

    #endregion

    #region Private Fields

    private readonly RequestDelegate _next;
    private readonly byte[]? _tokenHash;

    #endregion

    #region Constructors

    /// <summary>
    /// Captures the configured token without logging its value.
    /// </summary>
    /// <param name="next">The next component in the HTTP request pipeline.</param>
    /// <param name="options">The validated server configuration.</param>
    public OptionalBearerMiddleware(RequestDelegate next, IOptions<ProjectMemoryOptions> options)
    {
        _next = next;
        var token = options.Value.BearerToken;
        _tokenHash = string.IsNullOrEmpty(token) ? null : SHA256.HashData(Encoding.UTF8.GetBytes(token));
    }

    #endregion

    #region Properties

    #endregion

    #region Public Methods

    /// <summary>
    /// Rejects unauthorized requests before the MCP endpoint is invoked.
    /// </summary>
    /// <param name="context">The current HTTP request context.</param>
    /// <returns>A task representing request processing.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        if (_tokenHash is not null && context.Request.Path.StartsWithSegments("/mcp"))
        {
            var headers = context.Request.Headers.Authorization;
            if (headers.Count != 1 || !AuthenticationHeaderValue.TryParse(headers[0], out var header)
                || !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
                || header.Parameter is null
                || !CryptographicOperations.FixedTimeEquals(_tokenHash, SHA256.HashData(Encoding.UTF8.GetBytes(header.Parameter))))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer";
                return;
            }
        }

        await _next(context);
    }

    #endregion

    #region Private Methods

    #endregion
}
