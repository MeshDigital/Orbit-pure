using System;

namespace SLSKDONET.Services.Setup;

public enum SoulseekLoginOutcome
{
    Connected,
    AlreadyConnected,
    /// <summary>
    /// The server's INVALIDPASS. Soulseek has no separate sign-up: a new name is registered with the
    /// first password used, so this means either a mistyped password or a name someone else owns.
    /// </summary>
    PasswordRejected,
    InvalidUsername,
    ServerUnreachable,
    ServerBusy,
    CoolingDown,
    MissingInput,
    Failed,
}

public sealed record SoulseekLoginResult(SoulseekLoginOutcome Outcome, string Title, string Message)
{
    public bool IsSuccess => Outcome is SoulseekLoginOutcome.Connected or SoulseekLoginOutcome.AlreadyConnected;
}

/// <summary>
/// Turns what the connection lifecycle reports (a state plus a reason string such as
/// "login rejected: The server rejected login attempt: INVALIDPASS") into a result a person can
/// act on. Pure, so the mapping is unit-tested without a network.
/// </summary>
public static class SoulseekLoginResultMapper
{
    public static SoulseekLoginResult Connected(string username) =>
        new(SoulseekLoginOutcome.Connected, "Connected",
            $"Signed in as {username}. If this name was new, Soulseek has now registered it to you with this password.");

    public static SoulseekLoginResult AlreadyConnected(string username) =>
        new(SoulseekLoginOutcome.AlreadyConnected, "Already connected", $"ORBIT is already signed in as {username}.");

    public static SoulseekLoginResult MissingInput(string what) =>
        new(SoulseekLoginOutcome.MissingInput, "Missing " + what, $"Enter a {what} to test the connection.");

    public static SoulseekLoginResult CoolingDown() =>
        new(SoulseekLoginOutcome.CoolingDown, "Waiting to reconnect",
            "The server recently disconnected this session. ORBIT waits about a minute before signing in again. Try again shortly.");

    public static SoulseekLoginResult Timeout() =>
        new(SoulseekLoginOutcome.ServerUnreachable, "No answer from the server",
            "The Soulseek server didn't respond in time. Check your internet connection and try again.");

    /// <summary>Maps a lifecycle transition (<c>Current</c>, <c>Reason</c>) to a result.</summary>
    public static SoulseekLoginResult FromLifecycle(string currentState, string? reason, string username)
    {
        if (string.Equals(currentState, "LoggedIn", StringComparison.OrdinalIgnoreCase))
            return Connected(username);
        if (string.Equals(currentState, "CoolingDown", StringComparison.OrdinalIgnoreCase))
            return CoolingDown();
        return FromReason(reason ?? string.Empty);
    }

    public static SoulseekLoginResult FromException(Exception ex) =>
        ex is Soulseek.LoginRejectedException
            ? FromReason("login rejected: " + ex.Message)
            : FromReason("connect failed: " + ex.Message);

    public static SoulseekLoginResult FromReason(string reason)
    {
        var upper = reason.ToUpperInvariant();

        if (upper.Contains("INVALIDPASS"))
            return new(SoulseekLoginOutcome.PasswordRejected, "Wrong password, or the name is taken",
                "Soulseek rejected the password for this name. If it's your account, check the password. " +
                "If you meant to create a new account, this name already belongs to someone else: pick another one.");

        if (upper.Contains("INVALIDUSERNAME"))
            return new(SoulseekLoginOutcome.InvalidUsername, "Username not allowed",
                "Soulseek doesn't accept this username. Use letters and numbers, without spaces at the start or end, and keep it short.");

        if (upper.Contains("SVRFULL"))
            return new(SoulseekLoginOutcome.ServerBusy, "Server is full", "The Soulseek server isn't accepting new sessions right now. Try again in a few minutes.");

        if (upper.Contains("SVRPRIVATE") || upper.Contains("INVALIDVERSION"))
            return new(SoulseekLoginOutcome.Failed, "Server refused the client",
                "The Soulseek server refused this client version. Check for an ORBIT update.");

        if (upper.StartsWith("LOGIN REJECTED", StringComparison.Ordinal))
            return new(SoulseekLoginOutcome.Failed, "Sign-in rejected", "The server rejected the sign-in: " + Tail(reason));

        if (upper.Contains("LISTEN") && upper.Contains("PORT"))
            return new(SoulseekLoginOutcome.Failed, "Listen port in use",
                "Another program (often a second ORBIT or Soulseek client) holds the listen port. Close it, or change the port in Settings → Connection.");

        if (upper.Contains("TIMED OUT") || upper.Contains("TIMEOUT"))
            return Timeout();

        if (upper.Contains("REFUSED") || upper.Contains("NO SUCH HOST") || upper.Contains("UNREACHABLE")
            || upper.Contains("NAME OR SERVICE") || upper.Contains("NO ROUTE") || upper.Contains("SOCKET")
            || upper.Contains("NETWORK") || upper.Contains("FAILED TO CONNECT"))
            return new(SoulseekLoginOutcome.ServerUnreachable, "Server unreachable",
                "ORBIT couldn't reach the Soulseek server. Check your internet connection or firewall and try again." +
                (string.IsNullOrWhiteSpace(Tail(reason)) ? string.Empty : " (" + Tail(reason) + ")"));

        return new(SoulseekLoginOutcome.Failed, "Couldn't sign in",
            string.IsNullOrWhiteSpace(reason) ? "The connection closed before sign-in finished. Try again." : Tail(reason));
    }

    private static string Tail(string reason)
    {
        var idx = reason.IndexOf(':');
        return (idx >= 0 ? reason[(idx + 1)..] : reason).Trim();
    }
}
