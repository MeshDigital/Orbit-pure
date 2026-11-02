using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Configuration;
using SLSKDONET.Services.KnowledgeLifecycle;
using Xunit;

namespace SLSKDONET.Tests.Services.KnowledgeLifecycle;

/// <summary>The swarm is opt-in (rebuild plan D7): disabled config must not start any agent.</summary>
public class KnowledgeSwarmHostTests
{
    [Fact]
    public void EnableKnowledgeAgents_DefaultsOff()
    {
        Assert.False(new AppConfig().EnableKnowledgeAgents);
    }

    [Fact]
    public async Task Disabled_DoesNotStartAgents()
    {
        var agent = new FakeAgent();
        var host = new KnowledgeSwarmHost(new[] { agent }, new AppConfig { EnableKnowledgeAgents = false },
            NullLogger<KnowledgeSwarmHost>.Instance);

        await host.StartAsync(CancellationToken.None);
        await host.ExecuteTask!;

        Assert.False(agent.Started);
    }

    [Fact]
    public async Task Enabled_StartsAndStopsAgents()
    {
        var agent = new FakeAgent();
        var host = new KnowledgeSwarmHost(new[] { agent }, new AppConfig { EnableKnowledgeAgents = true },
            NullLogger<KnowledgeSwarmHost>.Instance);

        await host.StartAsync(CancellationToken.None);
        await agent.StartedSignal.Task;
        await host.StopAsync(CancellationToken.None);

        Assert.True(agent.Started);
        Assert.True(agent.Stopped);
    }

    private sealed class FakeAgent : IKnowledgeAgent
    {
        public string Name => "Fake";
        public bool Started { get; private set; }
        public bool Stopped { get; private set; }
        public TaskCompletionSource StartedSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StartAsync(CancellationToken ct)
        {
            Started = true;
            StartedSignal.TrySetResult();
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            Stopped = true;
            return Task.CompletedTask;
        }
    }
}
