using Microsoft.Extensions.Hosting;

namespace PuntersScraper.Web.Services;

/// <summary>Runs the developer-notice and update-availability checks on startup, then once an
/// hour for as long as the app is running — a long-lived unattended deployment (see
/// deploy/README.md) can go weeks without a restart, so a startup-only check (the desktop App's
/// approach) would leave it silent about a new notice or release for that whole time.</summary>
public sealed class PeriodicChecksHostedService(
    DeveloperNoticeService noticeService, UpdateAvailabilityService updateService) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await noticeService.RefreshAsync(stoppingToken);
            await updateService.RefreshAsync(stoppingToken);

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
