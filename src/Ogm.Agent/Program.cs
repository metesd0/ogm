using Microsoft.Extensions.Hosting.WindowsServices;
using Ogm.Agent;
using Ogm.Agent.Configuration;
using Ogm.Agent.Installer;
using Ogm.Agent.Print;
using Ogm.Agent.Queue;
using Ogm.Agent.Transport;

var isService = WindowsServiceHelpers.IsWindowsService();
var isConsoleMode = args.Contains("--console", StringComparer.OrdinalIgnoreCase) ||
                    args.Contains("--run", StringComparer.OrdinalIgnoreCase) ||
                    args.Contains("--debug", StringComparer.OrdinalIgnoreCase);

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = AgentInstaller.DisplayName;
});

var agentOptions = builder.Configuration
    .GetSection(AgentOptions.SectionName)
    .Get<AgentOptions>() ?? new AgentOptions();

// Eger Windows Servisi olarak baslatilmadiysa ve gelistirici konsol modunda degilse:
// Exe cift tiklandiginda otomatik kurulum/yonetim sihirbazi calisir.
if (!isService && !isConsoleMode)
{
    AgentInstaller.HandleInteractive(args, agentOptions);
    return;
}

builder.Services.AddSingleton(agentOptions);

builder.Services.AddSingleton(sp => AgentIdentity.LoadOrCreate(
    agentOptions.ResolveDataDirectory(),
    sp.GetRequiredService<ILogger<AgentIdentity>>()));

builder.Services.AddSingleton<OutboxQueue>();
builder.Services.AddSingleton<ServerClient>();
builder.Services.AddSingleton<PrinterManager>();
builder.Services.AddSingleton<SpoolWatcher>();
builder.Services.AddHostedService<AgentWorker>();

var host = builder.Build();
host.Run();
