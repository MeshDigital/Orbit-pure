using Xunit;

namespace SLSKDONET.Tests;

/// <summary>
/// Test classes that share process-wide state — ReactiveUI's RxApp schedulers, Avalonia's
/// Dispatcher, or timing-sensitive background workers — run in this collection so they never
/// overlap with each other or with the rest of the suite. Each passed on its own but failed at
/// random in full parallel runs (open-work plan item E4).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NonParallelCollection
{
    public const string Name = "Non-parallel (shared global state)";
}
