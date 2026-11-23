using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Services;
using SLSKDONET.Services.Enrichment;
using SLSKDONET.Services.Platform;
using Xunit;

namespace SLSKDONET.Tests.Services.Platform;

/// <summary>#313 / #314 / #315: secrets are encrypted with a random keyring-held key, never travel in argv, and persist on every OS.</summary>
public sealed class SecretStorageTests : IDisposable
{
    private readonly string _dir;

    public SecretStorageTests()
    {
        _dir = Path.Combine(Path.GetDirectoryName(SLSKDONET.Data.OrbitPaths.LibraryDbPath)!, "secrets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* intentional: best-effort cleanup of the isolated test folder */ }
    }

    /// <summary>An in-memory macOS <c>security</c> / Linux <c>secret-tool</c> that records every call.</summary>
    private sealed class FakeKeyringTools : ICliProcessRunner
    {
        public readonly Dictionary<string, string> Items = new();
        public readonly List<(string File, IReadOnlyList<string> Args, string? Stdin)> Calls = new();
        public bool Installed { get; set; } = true;

        public CliResult Run(string fileName, IReadOnlyList<string> args, string? stdin, TimeSpan timeout)
        {
            Calls.Add((fileName, args.ToArray(), stdin));
            if (!Installed) return CliResult.NotStarted($"{fileName}: not found");
            string Arg(string flag) => args[args.ToList().IndexOf(flag) + 1];

            if (fileName == "security")
            {
                if (args[0] == "-i")
                {
                    var parts = stdin!.Trim().Split(' ');
                    string P(string flag) => parts[Array.IndexOf(parts, flag) + 1];
                    Items[P("-a")] = Encoding.UTF8.GetString(Convert.FromHexString(P("-X")));
                    return new CliResult(true, 0, "", "");
                }
                if (args[0] == "find-generic-password")
                    return Items.TryGetValue(Arg("-a"), out var v) ? new CliResult(true, 0, v + "\n", "") : new CliResult(true, 44, "", "not found");
                if (args[0] == "delete-generic-password") { Items.Remove(Arg("-a")); return new CliResult(true, 0, "", ""); }
            }
            if (fileName == "secret-tool")
            {
                var account = Arg("account");
                switch (args[0])
                {
                    case "store": Items[account] = stdin!; return new CliResult(true, 0, "", "");
                    case "lookup": return Items.TryGetValue(account, out var v) ? new CliResult(true, 0, v, "") : new CliResult(true, 1, "", "");
                    case "clear": Items.Remove(account); return new CliResult(true, 0, "", "");
                }
            }
            return new CliResult(true, 1, "", "unexpected call");
        }

        public bool AnyArgumentContains(string secret) => Calls.Any(c => c.Args.Any(a => a.Contains(secret, StringComparison.Ordinal)));
    }

    private const string Token = "AQBx-refresh_token.with$odd\"chars\\and spaces";

    [Theory]
    [InlineData(KeyringKind.MacKeychain)]
    [InlineData(KeyringKind.SecretService)]
    public void Keyring_SecretsTravelOnStdinOnly_AndAreReadBack(KeyringKind kind)
    {
        var tools = new FakeKeyringTools();
        var keyring = new OsKeyring(kind, tools);

        Assert.True(keyring.TrySet("spotify_refresh_token", Token, out var error), error);
        Assert.Equal(Token, keyring.TryGet("spotify_refresh_token", out _));
        Assert.False(tools.AnyArgumentContains(Token));                 // never visible in `ps`
        Assert.Contains(tools.Calls, c => c.Stdin is not null);
        if (kind == KeyringKind.MacKeychain)
            Assert.DoesNotContain(tools.Calls, c => c.Stdin?.Contains(Token, StringComparison.Ordinal) == true); // hex, no quoting

        keyring.Delete("spotify_refresh_token");
        Assert.Null(keyring.TryGet("spotify_refresh_token", out _));
        Assert.Throws<ArgumentException>(() => keyring.TryGet("bad name; rm -rf", out _));
    }

    [Fact]
    public void Keyring_ThatDoesNotKeepTheSecret_ReportsFailure()
    {
        var tools = new FakeKeyringTools();
        var keyring = new OsKeyring(KeyringKind.MacKeychain, new DroppingRunner(tools));
        Assert.False(keyring.TrySet("x", "secret", out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    /// <summary>`security -i` that exits 0 but stores nothing.</summary>
    private sealed class DroppingRunner(FakeKeyringTools inner) : ICliProcessRunner
    {
        public CliResult Run(string f, IReadOnlyList<string> a, string? s, TimeSpan t) =>
            a.Count > 0 && a[0] == "-i" ? new CliResult(true, 0, "", "") : inner.Run(f, a, s, t);
    }

    [Fact]
    public void Gcm_UsesARandomKeyFromTheKeyring_AndDetectsTampering()
    {
        var tools = new FakeKeyringTools();
        var first = new GcmSecretProtector(new OsKeyring(KeyringKind.SecretService, tools), _dir, NullLogger.Instance);
        var cipher = first.Protect(Encoding.UTF8.GetBytes("hello"), "purpose");

        Assert.Equal("Secret Service", first.Backend);
        Assert.False(File.Exists(Path.Combine(_dir, "secrets.key")));       // the key lives in the keyring
        var key = tools.Items[GcmSecretProtector.DataKeyAccount];
        Assert.Equal(64, key.Length);
        Assert.NotEqual(Convert.ToHexString(LinuxTokenStorage.LegacyMachineKey()), key); // not derivable from machine facts

        var second = new GcmSecretProtector(new OsKeyring(KeyringKind.SecretService, tools), _dir, NullLogger.Instance);
        Assert.Equal("hello", Encoding.UTF8.GetString(second.Unprotect(cipher, "purpose")));

        Assert.ThrowsAny<CryptographicException>(() => second.Unprotect(cipher, "other-purpose"));
        var tampered = (byte[])cipher.Clone();
        tampered[^1] ^= 0x01;
        Assert.ThrowsAny<CryptographicException>(() => second.Unprotect(tampered, "purpose")); // authenticated
    }

    [Fact]
    public void Gcm_WithoutAKeyring_FallsBackToAUserOnlyKeyFile()
    {
        var tools = new FakeKeyringTools { Installed = false };
        var a = new GcmSecretProtector(new OsKeyring(KeyringKind.SecretService, tools), _dir, NullLogger.Instance);
        var cipher = a.Protect(Encoding.UTF8.GetBytes("x"), "p");
        Assert.True(File.Exists(Path.Combine(_dir, "secrets.key")));
        Assert.StartsWith("key file", a.Backend);

        var b = new GcmSecretProtector(new OsKeyring(KeyringKind.SecretService, tools), _dir, NullLogger.Instance);
        Assert.Equal("x", Encoding.UTF8.GetString(b.Unprotect(cipher, "p")));

        var elsewhere = Path.Combine(_dir, "other");
        var c = new GcmSecretProtector(new OsKeyring(KeyringKind.SecretService, tools), elsewhere, NullLogger.Instance);
        Assert.ThrowsAny<CryptographicException>(() => c.Unprotect(cipher, "p")); // a different random key
    }

    [Fact]
    public async Task Linux_SpotifyToken_RoundTrips_AndMigratesTheWeakLegacyFile()
    {
        var protector = new GcmSecretProtector(new OsKeyring(KeyringKind.SecretService, new FakeKeyringTools()), _dir, NullLogger.Instance);
        var legacy = Path.Combine(_dir, "qmusicslsk", "spotify_token.enc");
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        using (var aes = Aes.Create())
        {
            aes.Key = LinuxTokenStorage.LegacyMachineKey();
            aes.GenerateIV();
            var body = aes.CreateEncryptor().TransformFinalBlock(Encoding.UTF8.GetBytes("legacy-token"), 0, 12);
            File.WriteAllBytes(legacy, aes.IV.Concat(body).ToArray());
        }

        var storage = new LinuxTokenStorage(NullLogger<LinuxTokenStorage>.Instance, protector, _dir, legacy);
        Assert.Equal("legacy-token", await storage.LoadRefreshTokenAsync());
        Assert.False(File.Exists(legacy));                                  // the guessable-key file is gone
        Assert.True(File.Exists(Path.Combine(_dir, "spotify_token.dat")));
        Assert.Equal("legacy-token", await storage.LoadRefreshTokenAsync()); // now from the GCM file

        await storage.SaveRefreshTokenAsync(Token);
        Assert.Equal(Token, await storage.LoadRefreshTokenAsync());
        Assert.DoesNotContain("refresh_token", Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(_dir, "spotify_token.dat"))));
        await storage.DeleteRefreshTokenAsync();
        Assert.Null(await storage.LoadRefreshTokenAsync());
    }

    [Fact]
    public async Task MacOS_SpotifyToken_GoesToTheKeychainWithoutTouchingArgv()
    {
        var tools = new FakeKeyringTools();
        var storage = new MacOSTokenStorage(NullLogger<MacOSTokenStorage>.Instance, new OsKeyring(KeyringKind.MacKeychain, tools));

        await storage.SaveRefreshTokenAsync(Token);
        Assert.Equal(Token, tools.Items[MacOSTokenStorage.AccountName]);   // same account name as before: old tokens still found
        Assert.Equal(Token, await storage.LoadRefreshTokenAsync());
        Assert.False(tools.AnyArgumentContains(Token));

        await storage.DeleteRefreshTokenAsync();
        Assert.Null(await storage.LoadRefreshTokenAsync());

        tools.Installed = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.SaveRefreshTokenAsync(Token)); // a failed save is never reported as success
    }

    [Fact]
    public async Task Soulseek_AndMetadataKeys_PersistWithTheNonWindowsProtector()
    {
        var protector = new GcmSecretProtector(new OsKeyring(KeyringKind.SecretService, new FakeKeyringTools()), _dir, NullLogger.Instance);

        var slsk = new SoulseekCredentialService(NullLogger<SoulseekCredentialService>.Instance, protector, _dir);
        await slsk.SaveCredentialsAsync("dj", "p|ss word");
        Assert.True(slsk.LastSaveSucceeded);
        Assert.Equal(("dj", "p|ss word"), await new SoulseekCredentialService(NullLogger<SoulseekCredentialService>.Instance, protector, _dir).LoadCredentialsAsync());
        Assert.DoesNotContain("ss word", Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(_dir, "slsk_creds.dat"))));

        var keys = new MetadataCredentialStore(NullLogger<MetadataCredentialStore>.Instance, _dir, protector);
        Assert.True(keys.IsSecureStorageAvailable);
        Assert.True(keys.Set(MetadataCredentialStore.DiscogsToken, "discogs-secret"));
        Assert.Equal("discogs-secret", new MetadataCredentialStore(NullLogger<MetadataCredentialStore>.Instance, _dir, protector).Get(MetadataCredentialStore.DiscogsToken));
    }

    [Fact]
    public void Runner_ReportsAMissingToolInsteadOfThrowing()
    {
        var result = new CliProcessRunner().Run("orbit-no-such-tool-" + Guid.NewGuid().ToString("N"), Array.Empty<string>(), "x", TimeSpan.FromSeconds(5));
        Assert.False(result.Started);
        Assert.False(result.Succeeded);
    }
}
