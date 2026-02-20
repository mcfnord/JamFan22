using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace JamFan22.Services
{
    public class JammerHarvestService : BackgroundService
    {
        private readonly ILogger<JammerHarvestService> _logger;

        public JammerHarvestService(ILogger<JammerHarvestService> logger)
        {
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("JammerHarvestService is starting.");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await harvest.HarvestLoop2025(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in JammerHarvestService. Restarting loop in 30s.");
                    try
                    {
                        await Task.Delay(30000, stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }

            _logger.LogInformation("JammerHarvestService is stopping.");
        }
    }
}
