using Microsoft.Extensions.Hosting;

namespace TestWeb;

public sealed class EmailWorker : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
}
