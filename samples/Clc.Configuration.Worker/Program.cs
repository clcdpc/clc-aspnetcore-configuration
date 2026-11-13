using Clc.Configuration;

const string applicationDirectoryName = "clc-configuration-worker";
if (ClcConfigurationCommandLine.TryHandle(args, applicationDirectoryName, out var exitCode))
{
    return exitCode;
}

var builder = Host.CreateApplicationBuilder(args);
// Optional first-run bootstrap: creates a starter file, then throws so it can be edited.
builder.AddClcConfigurationWithBootstrap(applicationDirectoryName);
builder.Services.AddHostedService<Worker>();
using var host = builder.Build();
await host.RunAsync();
return 0;

internal sealed class Worker(ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Worker started with external CLC configuration.");
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }
}
