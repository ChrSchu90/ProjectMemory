namespace ProjectMemory.Hosting;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ProjectMemory.Configuration;
using ProjectMemory.Database;
using ProjectMemory.MemoryFiles;
using ProjectMemory.Project;
using ProjectMemory.Tools;
using ProjectMemory.Search;
using Serilog.Core;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;
using Serilog;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System;

/// <summary>
/// Composes the single service and its implemented administration tools.
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
        // Container interfaces are reached through an operator-controlled private port publication.
        if (string.IsNullOrEmpty(builder.Configuration[WebHostDefaults.ServerUrlsKey])
            && string.IsNullOrEmpty(builder.Configuration[WebHostDefaults.HttpPortsKey])
            && string.IsNullOrEmpty(builder.Configuration[WebHostDefaults.HttpsPortsKey])
            && !builder.Configuration.GetSection("Kestrel:Endpoints").Exists())
        {
            var container = builder.Configuration["DOTNET_RUNNING_IN_CONTAINER"];
            var address = string.Equals(container, "true", StringComparison.OrdinalIgnoreCase) || container == "1" ? "0.0.0.0" : "127.0.0.1";
            builder.WebHost.UseUrls($"http://{address}:8000");
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
                .WriteTo.Console(outputTemplate: "[{Timestamp:dd.MM.yyyy HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}", theme: AnsiConsoleTheme.Sixteen, applyThemeToRedirectedOutput: true);
                // ToDo: Should we limit log output? For now, no, since the log level can be configured by the end user and detailed logs are useful for diagnostic purposes.
                //.MinimumLevel.Override("Microsoft", logLevelSwitch.MinimumLevel > LogEventLevel.Debug ? LogEventLevel.Warning : logLevelSwitch.MinimumLevel)
                //.MinimumLevel.Override("ModelContextProtocol", LogEventLevel.Warning)
                //.MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Fatal)
                //.MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Warning);
        });

        // Register the existing SQLite persistence and routing services.
        builder.Services.AddPersistence();
        builder.Services.AddSingleton<ProjectManager>();
        builder.Services.AddSingleton<MemoryFileStore>();
        builder.Services.AddSingleton<MemoryManager>();
        builder.Services.AddSingleton<RipgrepSearchService>();
        builder.Services.AddSingleton<ProjectDeletion>();

        // Register the HTTP MCP transport and the project and context administration tools.
        builder.Services.AddMcpServer(options => options.ServerInfo = new Implementation { Name = "ProjectMemory", Version = Version })
            .WithHttpTransport(options =>
            {
                // Stateless mode does not require server-to-client sampling or elicitation.
                options.SessionMode = HttpServerSessionMode.Stateless;
            })
            .WithTools<AdministrationTools>(AdministrationTools.SerializerOptions)
            .WithTools<MemoryTools>(AdministrationTools.SerializerOptions)
            .WithRequestFilters(filters => filters.AddCallToolFilter(AdministrationTools.ValidateArguments));

        // Register the storage readiness check; liveness will exclude storage checks.
        builder.Services.AddSingleton<DataRootHealthCheck>();
        builder.Services.AddHealthChecks().AddCheck<DataRootHealthCheck>("storage", tags: ["ready"]);

        // Build the application after all services have been registered.
        var app = builder.Build();
        try
        {
            // Validate configuration and storage before initializing the database.
            _ = app.Services.GetRequiredService<IOptions<ProjectMemoryOptions>>().Value;
            var stopping = app.Lifetime.ApplicationStopping;
            await app.Services.GetRequiredService<RipgrepSearchService>().VerifyBinaryAsync(stopping);
            var inventory = app.Services.GetRequiredService<MemoryFileStore>()
                .Preflight(app.Services.GetRequiredService<DatabasePaths>(), stopping);
            var storage = app.Services.GetRequiredService<DataRootHealthCheck>();
            if ((await storage.CheckHealthAsync(new HealthCheckContext(), stopping)).Status != HealthStatus.Healthy)
                throw new InvalidOperationException("Data storage is unavailable.");

            // Complete database initialization before the MCP endpoint can accept requests.
            await app.Services.MigrateDatabaseAsync(stopping);
            await app.Services.GetRequiredService<ProjectDeletion>().RecoverAsync(inventory, stopping);

            // Protect MCP requests and map the private operational and transport endpoints.
            app.UseMiddleware<OptionalBearerMiddleware>();
            app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
            app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

            AdministrationTools.ConfigureSchemas(app.Services.GetServices<McpServerTool>());
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
