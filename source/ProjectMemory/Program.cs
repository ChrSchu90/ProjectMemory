using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProjectMemory.Hosting;
using ProjectMemory.MemoryFiles;
using ProjectMemory.Search;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;
using System;
using System.Globalization;
using System.Threading;

// Force English exception messages before initializing the host.
CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;

// Initialize logging before creating the builder so early startup failures are observable.
var logLevelSwitch = new LoggingLevelSwitch { MinimumLevel = LogEventLevel.Information };
Log.Logger = new LoggerConfiguration().MinimumLevel.ControlledBy(logLevelSwitch)
    .WriteTo.Console(outputTemplate: "[{Timestamp:dd.MM.yyyy HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}", theme: AnsiConsoleTheme.Sixteen, applyThemeToRedirectedOutput: true)
    .CreateLogger();
Log.Information("Starting ProjectMemory v{Version}", ServerHost.Version);

try
{
    // Create the builder and share the startup log-level switch with the host.
    var builder = WebApplication.CreateBuilder(args);
    builder.Services.AddSingleton(logLevelSwitch);

    // Compose and initialize the host before accepting requests.
    var app = await ServerHost.CreateAsync(builder);
    await app.RunAsync();
    return 0;
}
catch (OptionsValidationException exception)
{
    // These validation messages describe configuration rules without echoing configured values.
    Log.Fatal("Server configuration is invalid: {ValidationErrors}", string.Join(" ", exception.Failures));
    return 1;
}
catch (MemoryFileException exception) when (exception.Code == "SEARCH_UNAVAILABLE")
{
    Log.Fatal("Search startup validation failed: cannot execute or validate rg. Install ripgrep v{Version} or newer with PCRE2 and ensure rg is on PATH.", RipgrepSearchService.MinimumRipgrepVersion);
    return 1;
}
catch (Exception exception)
{
    // Configuration and storage exceptions can contain secrets or absolute host paths.
    Log.Fatal("Server startup or execution failed ({ErrorType}). Check configuration and storage permissions.", exception.GetType().Name);
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
