using StrongLink.Worker.Services;

namespace StrongLink.Worker;

public class Worker : BackgroundService
{
    private readonly IBotLifetimeService _botLifetimeService;
    private readonly ILogger<Worker> _logger;

    public Worker(IBotLifetimeService botLifetimeService, ILogger<Worker> logger)
    {
        _botLifetimeService = botLifetimeService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Strong Link worker starting");
        await _botLifetimeService.StartAsync(stoppingToken);

        stoppingToken.Register(() => _logger.LogInformation("Cancellation requested for Strong Link worker"));

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Strong Link worker stopping");
        await _botLifetimeService.StopAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }
}
