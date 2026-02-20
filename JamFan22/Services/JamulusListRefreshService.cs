using JamFan22.Pages;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace JamFan22.Services
{
    public class JamulusListRefreshService : BackgroundService
    {
        private readonly ILogger<JamulusListRefreshService> _logger;

        public JamulusListRefreshService(ILogger<JamulusListRefreshService> logger)
        {
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("JamulusListRefreshService is starting.");

            // Wait a bit for the app to start up, similar to Task.Delay(6000) in Program.cs
            try 
            {
                await Task.Delay(6000, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await IndexModel.RefreshThreadTask(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in JamulusListRefreshService. Restarting loop in 30s.");
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

            _logger.LogInformation("JamulusListRefreshService is stopping.");
        }
    }
}
