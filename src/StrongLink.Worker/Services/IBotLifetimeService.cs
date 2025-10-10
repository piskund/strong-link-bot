namespace StrongLink.Worker.Services;

public interface IBotLifetimeService
{
    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}

