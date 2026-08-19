using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TheAgent;
using Xianix.Agent;
using Xianix.Workflows;
using Xianix.Orchestrator;
using Xianix.Rules;
using Xianix.Rules.Schedule;
using XiansInfraLoggerFactory = Xians.Lib.Common.Infrastructure.LoggerFactory;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// NOTE: do NOT use `using var` here. The ProcessExit handler below runs on
// AppDomain unload, which fires AFTER Main exits — and Main exit triggers the
// `using` dispose. The handler would then call Cancel() on a disposed token
// source and throw ObjectDisposedException as an unhandled exception. We instead
// manually dispose in the finally block AFTER unsubscribing the handlers, and
// wrap the in-handler Cancel() calls in a defensive try/catch so that even a
// late-arriving ProcessExit (e.g. forced by Environment.Exit before finally
// runs) cannot crash the process on its way out.
var cts = new CancellationTokenSource();

void SafeCancel()
{
    try { cts.Cancel(); }
    catch (ObjectDisposedException) { /* shutdown is already in progress */ }
}

ConsoleCancelEventHandler cancelKeyHandler = (_, e) =>
{
    e.Cancel = true;
    SafeCancel();
};
EventHandler processExitHandler = (_, _) => SafeCancel();

Console.CancelKeyPress         += cancelKeyHandler;
AppDomain.CurrentDomain.ProcessExit += processExitHandler;

ServiceProvider? serviceProvider = null;
ILogger? logger = null;

try
{
    var appEnv = Environment.GetEnvironmentVariable("APP_ENV");
    var envFile = string.IsNullOrWhiteSpace(appEnv) ? ".env" : $".env.{appEnv}";
    EnvConfig.Load(envFile);
    EnvConfig.ValidateRequiredVariables();

    PrintBanner();

    serviceProvider = ConfigureServices();
    logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Program");
    logger.LogInformation("Services configured. Environment: {AppEnv}.", appEnv ?? "default");

    var agent = serviceProvider.GetRequiredService<XianixAgent>();
    await agent.RunAsync(cts.Token);
}
catch (OperationCanceledException)
{
    logger?.LogInformation("Shutdown requested. Exiting gracefully.");
}
catch (InvalidOperationException ex) when (ex.Message.Contains("environment variable"))
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine($"[FATAL] Configuration error: {ex.Message}");
    Console.ResetColor();
    Environment.ExitCode = 1;
}
catch (Exception ex)
{
    if (logger is not null)
    {
        logger.LogCritical(ex, "Unhandled exception — agent shutting down.");
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"[FATAL] {ex}");
        Console.ResetColor();
    }
    Environment.ExitCode = 1;
}
finally
{
    if (serviceProvider is not null)
    {
        logger?.LogInformation("Disposing services.");
        await serviceProvider.DisposeAsync();
    }

    // Unsubscribe before disposing the CancellationTokenSource so a late
    // ProcessExit firing on AppDomain unload doesn't reach a disposed instance.
    // SafeCancel's try/catch is the second line of defence; this is the first.
    Console.CancelKeyPress         -= cancelKeyHandler;
    AppDomain.CurrentDomain.ProcessExit -= processExitHandler;
    cts.Dispose();
}

return;

// ── Local functions ──────────────────────────────────────────────────────────

static void PrintBanner()
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("╔══════════════════════════════╗");
    Console.WriteLine("║     The Xianix Agent v1.0    ║");
    Console.WriteLine("╚══════════════════════════════╝");
    Console.ResetColor();
}

static ServiceProvider ConfigureServices()
{
    var minLogLevel = Enum.TryParse<LogLevel>(
        EnvConfig.Get("LOG_LEVEL", "Information"), ignoreCase: true, out var parsed)
        ? parsed
        : LogLevel.Information;

    var services = new ServiceCollection();

    services.AddSingleton(_ => XiansInfraLoggerFactory.Instance);

    services.AddLogging(builder =>
    {
        builder.AddConsole();
        builder.SetMinimumLevel(minLogLevel);
    });

    services.AddSingleton<IWebhookRulesEvaluator, WebhookRulesEvaluator>();
    services.AddSingleton<IWebhookDeduplicationGuard, WebhookDeduplicationGuard>();
    services.AddSingleton<IEventOrchestrator, EventOrchestrator>();
    services.AddSingleton<XianixAgent>();

    return services.BuildServiceProvider();
}
