using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TheAgent;
using Xianix.Agent;
using Xianix.Orchestrator;
using Xianix.Rules;
using XiansInfraLoggerFactory = Xians.Lib.Common.Infrastructure.LoggerFactory;

Console.OutputEncoding = System.Text.Encoding.UTF8;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

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
    services.AddSingleton<IEventOrchestrator, EventOrchestrator>();
    services.AddSingleton<XianixAgent>();

    return services.BuildServiceProvider();
}
