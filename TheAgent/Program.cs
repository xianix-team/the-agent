using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TheAgent;
using Xianix.Agent;
using Xianix.Orchestrator;
using Xianix.Rules;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// Load .env by traversing up the directory tree (DotNetEnv)
EnvConfig.Load();

Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine("╔══════════════════════════════╗");
Console.WriteLine("║     The Xianix Agent v1.0    ║");
Console.WriteLine("╚══════════════════════════════╝");
Console.ResetColor();

var services = new ServiceCollection();

services.AddLogging(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Debug);
});

// ContainerActivities is instantiated directly by the Xians platform (no DI).
// Only application-level services that XianixAgent depends on are registered here.
services.AddSingleton<IWebhookRulesEvaluator, WebhookRulesEvaluator>();
services.AddSingleton<IEventOrchestrator, EventOrchestrator>();
services.AddSingleton<XianixAgent>();

var serviceProvider = services.BuildServiceProvider();

var agent = serviceProvider.GetRequiredService<XianixAgent>();
await agent.RunAsync();
