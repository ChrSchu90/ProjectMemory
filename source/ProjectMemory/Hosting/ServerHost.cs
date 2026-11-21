namespace ProjectMemory.Hosting;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ProjectMemory.Configuration;
using ProjectMemory.Database;
using ProjectMemory.Operations;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

/// <summary>
/// Composes the single service without publishing later milestone tools.
/// </summary>
internal static class ServerHost
{
    #region Static Fields

    #endregion

    #region Private Fields

    #endregion

    #region Constructors

    #endregion

    #region Properties

    /// <summary>
    /// Gets the version.
    /// </summary>
    internal static string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    #endregion

    #region Public Methods

    /// <summary>
    /// Builds and initializes the service before opening its HTTP listener.
    /// </summary>
    /// <param name="builder">The application builder supplying configuration and services.</param>
    /// <returns>The initialized application ready to start its listener.</returns>
    public static async Task<WebApplication> CreateAsync(WebApplicationBuilder builder)
    {
        // Use private HTTP unless the operator explicitly configures a listener.
        if (string.IsNullOrEmpty(builder.Configuration["urls"]) && !builder.Configuration.GetSection("Kestrel:Endpoints").Exists())
        {
            builder.WebHost.UseUrls("http://127.0.0.1:8000");
        }

        // Register the configuration section and validate it before accepting requests.
        builder.Services.AddOptions<ProjectMemoryOptions>()
            .Bind(builder.Configuration.GetSection(ProjectMemoryOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.DataDirectory) && Path.IsPathFullyQualified(options.DataDirectory) && Directory.Exists(options.DataDirectory), "DataDirectory must be an existing absolute directory.")
            .Validate(options => Enum.TryParse<LogEventLevel>(options.LogLevel, true, out var level) && Enum.IsDefined(level), "LogLevel must be a valid log level.")
            .Validate(options => string.IsNullOrEmpty(options.BearerToken) || options.BearerToken.All(c => char.IsAsciiLetterOrDigit(c) || "-._~+/=".Contains(c)), "BearerToken must contain only bearer-token characters.")
            .ValidateOnStart();

        // Keep the process log-level switch; isolated test hosts receive their own instance.
        builder.Services.TryAddSingleton<LoggingLevelSwitch>();
        builder.Services.AddSerilog((services, configuration) =>
        {
            var options = services.GetRequiredService<IOptions<ProjectMemoryOptions>>().Value;
            var logLevelSwitch = services.GetRequiredService<LoggingLevelSwitch>();
            logLevelSwitch.MinimumLevel = Enum.Parse<LogEventLevel>(options.LogLevel, true);

            configuration.MinimumLevel.ControlledBy(logLevelSwitch)
                .WriteTo.Console(outputTemplate: "[{Timestamp:dd.MM.yyyy HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}", theme: AnsiConsoleTheme.Sixteen, applyThemeToRedirectedOutput: true)

                // Diagnostic logs from ASP.NET Core and EF Core are too verbose for normal operation.
                .MinimumLevel.Override("Microsoft", logLevelSwitch.MinimumLevel > LogEventLevel.Debug ? LogEventLevel.Warning : logLevelSwitch.MinimumLevel)
                .MinimumLevel.Override("Microsoft.EntityFrameworkCore", logLevelSwitch.MinimumLevel > LogEventLevel.Debug ? LogEventLevel.Fatal : logLevelSwitch.MinimumLevel);
        });

        // Register the existing SQLite persistence and routing services.
        builder.Services.AddPersistence();

        // Register the HTTP MCP transport and truthful M1 tool discovery.
        builder.Services.AddMcpServer(options => options.ServerInfo = new Implementation { Name = "ProjectMemory", Version = Version })
            .WithHttpTransport(options =>
            {
                // Stateless mode does not require server-to-client sampling or elicitation.
                options.SessionMode = HttpServerSessionMode.Stateless;
            })
            .WithListToolsHandler((_, _) => new ValueTask<ListToolsResult>(new ListToolsResult { Tools = [] }));

        // Register the storage readiness check; liveness will exclude storage checks.
        builder.Services.AddSingleton<DataRootHealthCheck>();
        builder.Services.AddHealthChecks().AddCheck<DataRootHealthCheck>("storage", tags: ["ready"]);

        // Build the application after all services have been registered.
        var app = builder.Build();
        try
        {
            // Validate configuration and storage before initializing the database.
            _ = app.Services.GetRequiredService<IOptions<ProjectMemoryOptions>>().Value;
            var storage = app.Services.GetRequiredService<DataRootHealthCheck>();
            if ((await storage.CheckHealthAsync(new HealthCheckContext())).Status != HealthStatus.Healthy)
                throw new InvalidOperationException("Data storage is unavailable.");

            // Complete database initialization before the MCP endpoint can accept requests.
            await app.Services.MigrateDatabaseAsync();

            // Protect MCP requests and map the private operational and transport endpoints.
            app.UseMiddleware<OptionalBearerMiddleware>();
            app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
            app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
            app.MapMcp("/mcp");
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    #endregion

    #region Private Methods

    #endregion
}
