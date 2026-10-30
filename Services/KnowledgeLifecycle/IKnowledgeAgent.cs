using System.Threading;
using System.Threading.Tasks;

namespace SLSKDONET.Services.KnowledgeLifecycle;

/// <summary>
/// Phase 2 (Knowledge Lifecycle): common lifecycle contract for the local agent swarm. Each agent
/// subscribes to its triggers on <see cref="StartAsync"/> and releases them on <see cref="StopAsync"/>.
/// </summary>
public interface IKnowledgeAgent
{
    string Name { get; }

    Task StartAsync(CancellationToken ct);

    Task StopAsync();
}
