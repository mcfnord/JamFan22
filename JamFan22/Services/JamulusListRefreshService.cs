using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace JamFan22.Services
{
    public class JamulusListRefreshService : BackgroundService
    {
        private readonly ILogger<JamulusListRefreshService> _logger;
        private readonly JamulusCacheManager _cacheManager;

        public JamulusListRefreshService(ILogger<JamulusListRefreshService> logger, JamulusCacheManager cacheManager)
        {
            _logger       = logger;
            _cacheManager = cacheManager;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("JamulusListRefreshService is starting.");

            try { await Task.Delay(6000, stoppingToken); }
            catch (OperationCanceledException) { return; }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await _cacheManager.RefreshThreadTask(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in JamulusListRefreshService. Restarting loop in 30s.");
                    try { await Task.Delay(30000, stoppingToken); }
                    catch (OperationCanceledException) { break; }
                }
            }

            _logger.LogInformation("JamulusListRefreshService is stopping.");
        }
    }
}
