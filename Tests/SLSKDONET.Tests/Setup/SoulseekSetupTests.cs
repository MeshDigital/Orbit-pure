using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Setup;
using Xunit;

namespace SLSKDONET.Tests.Setup;

public class SoulseekSetupTests
{
    [Theory]
    [InlineData("login rejected: The server rejected login attempt: INVALIDPASS", SoulseekLoginOutcome.PasswordRejected)]
    [InlineData("login rejected: The server rejected login attempt: INVALIDUSERNAME", SoulseekLoginOutcome.InvalidUsername)]
    [InlineData("login rejected: The server rejected login attempt: SVRFULL", SoulseekLoginOutcome.ServerBusy)]
    [InlineData("connect failed: Failed to connect: No such host is known.", SoulseekLoginOutcome.ServerUnreachable)]
    [InlineData("connect failed: Failed to connect: No connection could be made because the target machine actively refused it.", SoulseekLoginOutcome.ServerUnreachable)]
    [InlineData("connect failed: The wait timed out after 30000 milliseconds", SoulseekLoginOutcome.ServerUnreachable)]
    [InlineData("connect failed: Failed to start listening on port 49998", SoulseekLoginOutcome.Failed)]
    [InlineData("", SoulseekLoginOutcome.Failed)]
    public void Reason_MapsToOutcome(string reason, SoulseekLoginOutcome expected)
    {
        var result = SoulseekLoginResultMapper.FromLifecycle("Disconnected", reason, "dj");
        Assert.Equal(expected, result.Outcome);
        Assert.False(result.IsSuccess);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public void WrongPassword_ExplainsNameMayBeTaken()
    {
        var result = SoulseekLoginResultMapper.FromReason("login rejected: The server rejected login attempt: INVALIDPASS");
        Assert.Contains("taken", result.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("password", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoggedInAndCoolingDown_Map()
    {
        Assert.Equal(SoulseekLoginOutcome.Connected, SoulseekLoginResultMapper.FromLifecycle("LoggedIn", "", "dj").Outcome);
        Assert.True(SoulseekLoginResultMapper.FromLifecycle("LoggedIn", "", "dj").IsSuccess);
        Assert.Equal(SoulseekLoginOutcome.CoolingDown, SoulseekLoginResultMapper.FromLifecycle("CoolingDown", "kicked", "dj").Outcome);
    }

    [Fact]
    public void LoginRejectedException_MapsToPasswordRejected()
    {
        var ex = new Soulseek.LoginRejectedException("The server rejected login attempt: INVALIDPASS");
        Assert.Equal(SoulseekLoginOutcome.PasswordRejected, SoulseekLoginResultMapper.FromException(ex).Outcome);
    }

    [Fact]
    public async Task Gateway_WrongPassword_ReportsRejection_AndRestoresUsername()
    {
        using var sandbox = new SetupSandbox();
        sandbox.Config.Username = "previous";
        var bus = new EventBusService();
        var lifecycle = new FakeLifecycle(bus, sandbox.Config) { Outcome = ("Disconnected", "login rejected: The server rejected login attempt: INVALIDPASS") };
        var gateway = NewGateway(sandbox, bus, lifecycle);

        var result = await gateway.TestLoginAsync("taken-name", "pw", CancellationToken.None);

        Assert.Equal(SoulseekLoginOutcome.PasswordRejected, result.Outcome);
        Assert.Equal("taken-name", lifecycle.UsernameDuringConnect);
        Assert.Equal("previous", sandbox.Config.Username); // failed test leaves nothing behind
        Assert.False(System.IO.File.Exists(sandbox.ConfigPath)); // and saves nothing
    }

    [Fact]
    public async Task Gateway_Success_ConnectsWithoutSavingUntilConfirmed()
    {
        using var sandbox = new SetupSandbox();
        var bus = new EventBusService();
        var lifecycle = new FakeLifecycle(bus) { Outcome = ("LoggedIn", "logged in") };
        var credentials = new RecordingCredentials();
        var gateway = NewGateway(sandbox, bus, lifecycle, credentials);

        var result = await gateway.TestLoginAsync("newname", "secret", CancellationToken.None);
        Assert.Equal(SoulseekLoginOutcome.Connected, result.Outcome);
        Assert.False(System.IO.File.Exists(sandbox.ConfigPath));
        Assert.Null(credentials.Saved);

        await gateway.SaveAccountAsync(new SoulseekAccountChoice("newname", "secret", RememberAndAutoConnect: true));
        var loaded = new SLSKDONET.Configuration.ConfigManager(sandbox.ConfigPath).Load();
        Assert.Equal("newname", loaded.Username);
        Assert.True(loaded.RememberPassword);
        Assert.True(loaded.AutoConnectEnabled);
        Assert.Equal(("newname", "secret"), credentials.Saved);
    }

    [Fact]
    public async Task Gateway_NoAnswer_TimesOutAsUnreachable()
    {
        using var sandbox = new SetupSandbox();
        var bus = new EventBusService();
        var lifecycle = new FakeLifecycle(bus) { Outcome = null };
        var gateway = new SoulseekSetupGateway(sandbox.Config, sandbox.ConfigManager, lifecycle, bus, new RecordingCredentials(),
            NullLogger<SoulseekSetupGateway>.Instance, TimeSpan.FromMilliseconds(100));

        var result = await gateway.TestLoginAsync("dj", "pw", CancellationToken.None);
        Assert.Equal(SoulseekLoginOutcome.ServerUnreachable, result.Outcome);
    }

    [Fact]
    public async Task Gateway_MissingInput_DoesNotConnect()
    {
        using var sandbox = new SetupSandbox();
        var bus = new EventBusService();
        var lifecycle = new FakeLifecycle(bus);
        var gateway = NewGateway(sandbox, bus, lifecycle);

        Assert.Equal(SoulseekLoginOutcome.MissingInput, (await gateway.TestLoginAsync("  ", "pw", CancellationToken.None)).Outcome);
        Assert.Equal(SoulseekLoginOutcome.MissingInput, (await gateway.TestLoginAsync("dj", "", CancellationToken.None)).Outcome);
        Assert.Equal(0, lifecycle.ConnectCalls);
    }

    private static SoulseekSetupGateway NewGateway(SetupSandbox sandbox, IEventBus bus, IConnectionLifecycleService lifecycle, ISoulseekCredentialService? credentials = null) =>
        new(sandbox.Config, sandbox.ConfigManager, lifecycle, bus, credentials ?? new RecordingCredentials(),
            NullLogger<SoulseekSetupGateway>.Instance, TimeSpan.FromSeconds(5));

    /// <summary>Stands in for ConnectionLifecycleService: publishes the transition the real one would.</summary>
    private sealed class FakeLifecycle : IConnectionLifecycleService
    {
        private readonly IEventBus _bus;
        private readonly SLSKDONET.Configuration.AppConfig? _config;
        public FakeLifecycle(IEventBus bus, SLSKDONET.Configuration.AppConfig? config = null) { _bus = bus; _config = config; }
        public (string State, string Reason)? Outcome { get; set; } = ("LoggedIn", "ok");
        public int ConnectCalls { get; private set; }
        public string? UsernameDuringConnect { get; private set; }
        public ConnectionLifecycleState CurrentState { get; private set; } = ConnectionLifecycleState.Disconnected;
        public bool AutoReconnectEnabled { get; set; }

        public Task RequestConnectAsync(string? password, string? correlationId = null, CancellationToken ct = default)
        {
            ConnectCalls++;
            UsernameDuringConnect = _config?.Username;
            if (Outcome is { } o)
            {
                CurrentState = Enum.Parse<ConnectionLifecycleState>(o.State);
                _bus.Publish(new ConnectionLifecycleStateChangedEvent("LoggingIn", o.State, o.Reason, correlationId));
            }
            return Task.CompletedTask;
        }

        public Task RequestDisconnectAsync(string reason, string? correlationId = null) { CurrentState = ConnectionLifecycleState.Disconnected; return Task.CompletedTask; }
        public void NotifyManualDisconnect() { }
    }

    private sealed class RecordingCredentials : ISoulseekCredentialService
    {
        public (string, string)? Saved { get; private set; }
        public Task SaveCredentialsAsync(string username, string password) { Saved = (username, password); return Task.CompletedTask; }
        public Task<(string? Username, string? Password)> LoadCredentialsAsync() => Task.FromResult<(string?, string?)>((null, null));
        public Task DeleteCredentialsAsync() { Saved = null; return Task.CompletedTask; }
    }
}
