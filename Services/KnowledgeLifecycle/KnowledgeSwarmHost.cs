using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Services.KnowledgeLifecycle;

/// <summary>
/// Phase 2 (Knowledge Lifecycle): hosts the agent swarm as a single <see cref="BackgroundService"/>.
/// Registers with the DI container via <c>AddHostedService&lt;KnowledgeSwarmHost&gt;()</c>; the app's
/// existing startup/shutdown path (<c>GetServices&lt;IHostedService&gt;()</c>) drives
/// <see cref="StartAsync"/> and <see cref="StopAsync"/>, so the swarm never orphans threads on exit.
/// </summary>
public sealed class KnowledgeSwarmHost : BackgroundService
{
    private readonly IEnumerable<IKnowledgeAgent> _agents;
    private readonly ILogger<KnowledgeSwarmHost> _logger;

    public KnowledgeSwarmHost(IEnumerable<IKnowledgeAgent> agents, ILogger<KnowledgeSwarmHost> logger)
    {
        _agents = agents;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
