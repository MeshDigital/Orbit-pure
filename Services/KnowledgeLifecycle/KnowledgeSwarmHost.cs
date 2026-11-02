using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;

namespace SLSKDONET.Services.KnowledgeLifecycle;

/// <summary>
/// Phase 2 (Knowledge Lifecycle): hosts the agent swarm as a single <see cref="BackgroundService"/>.
/// Registers with the DI container via <c>AddHostedService&lt;KnowledgeSwarmHost&gt;()</c>; the app's
/// existing startup/shutdown path (<c>GetServices&lt;IHostedService&gt;()</c>) drives
/// <see cref="StartAsync"/> and <see cref="StopAsync"/>, so the swarm never orphans threads on exit.
/// <para>
/// Off by default (<see cref="AppConfig.EnableKnowledgeAgents"/>, Settings → Advanced): nothing in the
/// UI consumes the agents' output yet, so scanning after every analysis would only cost cycles.
/// Read once at startup; changing the setting takes effect after a restart.
/// </para>
/// </summary>
public sealed class KnowledgeSwarmHost : BackgroundService
{
    private readonly IEnumerable<IKnowledgeAgent> _agents;
    private readonly AppConfig _config;
    private readonly ILogger<KnowledgeSwarmHost> _logger;

    public KnowledgeSwarmHost(IEnumerable<IKnowledgeAgent> agents, AppConfig config, ILogger<KnowledgeSwarmHost> logger)
    {
        _agents = agents;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.EnableKnowledgeAgents)
        {
            _logger.LogInformation("[KnowledgeSwarm] disabled (Settings → Advanced → Knowledge agents); not starting agents");
            return;
        }

        foreach (var agent in _agents)
        {
            _logger.LogInformation("[KnowledgeSwarm] starting {Agent}", agent.Name);
            await agent.StartAsync(stoppingToken).ConfigureAwait(false);
        }

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path.
        }

        foreach (var agent in _agents)
        {
            try
            {
                _logger.LogInformation("[KnowledgeSwarm] stopping {Agent}", agent.Name);
                await agent.StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[KnowledgeSwarm] failed to stop {Agent}", agent.Name);
            }
        }
    }
}
