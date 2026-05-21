using Microsoft.Extensions.Hosting;

namespace TestWeb;

public sealed class PollingWorker : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
}
