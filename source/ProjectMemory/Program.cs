using System;
using System.Globalization;
using System.Reflection;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore;
using ProjectMemory.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;

// Force english exception messages
CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;

// Init logging
var logLevelSwitch = new LoggingLevelSwitch { MinimumLevel = LogEventLevel.Information };
Log.Logger = new LoggerConfiguration().MinimumLevel.ControlledBy(logLevelSwitch)
    .WriteTo.Console(outputTemplate: "[{Timestamp:dd.MM.yyyy HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}", theme: AnsiConsoleTheme.Sixteen, applyThemeToRedirectedOutput: true)
    .CreateLogger();
Log.Information($"Starting Server v{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}");

var builder = WebApplication.CreateBuilder(args);

// Register logging with Serilog. This will replace the default logging provider.
builder.Services.AddSerilog();
builder.Services.AddSingleton(logLevelSwitch);

// Set log level from configuration, defaulting to Information if not configured or invalid.
var configuredLogLevel = builder.Configuration[$"{ProjectMemoryOptions.SectionName}:{nameof(ProjectMemoryOptions.LogLevel)}"] ?? ProjectMemoryOptions.DefaultLogLevel;
if (Enum.TryParse<LogEventLevel>(configuredLogLevel, true, out var minimumLogLevel))
    logLevelSwitch.MinimumLevel = minimumLogLevel;
else
    Log.Error($"Invalid log level '{configuredLogLevel}', allowed values are '{string.Join(", ", Enum.GetValues<LogEventLevel>())}', fallback to default level '{logLevelSwitch.MinimumLevel}'!");

// Register the configuration section and validate it on startup.
builder.Services.AddOptions<ProjectMemoryOptions>()
    .Bind(builder.Configuration.GetSection(ProjectMemoryOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.DataDirectory), $"{ProjectMemoryOptions.SectionName}__{nameof(ProjectMemoryOptions.DataDirectory)} must be configured.")
    //.Validate(options => Enum.TryParse<LogEventLevel>(options.LogLevel, true, out _), "LogLevel must be a valid Serilog log event level.")
    .ValidateOnStart();

// Register the lifecycle database. Schema migrations are applied to an isolated copy during startup before MCP requests are accepted.
//builder.Services.AddPersistence(); // ToDo: Database

// Add the MCP services: the transport to use (http) and the tools to register.
builder.Services
    .AddMcpServer()
    .WithHttpTransport(options =>
    {
        // Stateless mode is recommended for servers that don't need
        // server-to-client requests like sampling or elicitation.
        // See https://csharp.sdk.modelcontextprotocol.io/concepts/transports/transports.html for details.
        options.SessionMode = HttpServerSessionMode.Stateless;
    })
    //.WithTools<RoutingTools>() // ToDo: Add tools
    ;

// Register the health checks for liveness and readiness probes.
//builder.Services.AddHealthChecks().AddCheck<HealthCheck>(); // ToDo: health check

var app = builder.Build();

// Ensure the lifecycle database is fully initialized or migrated before the MCP endpoint starts.
//await app.Services.MigrateDatabaseAsync(); // ToDo: Database

// Map the health check endpoints for liveness and readiness probes.
//app.MapProjectMemoryProxyHealthEndpoints(); // ToDo: health check

app.MapMcp("/mcp");
app.Run();
