using TriffView.Alerts;

namespace TriffView.Tests;

public class TriffAlertsParsingTests
{
    // Parsing only: settings remain disabled, so no log watcher, UI, or flash runs.
    [Theory]
    [InlineData("Warp scramble attempt")]
    [InlineData("Warp disruption attempt")]
    public void SharedFleetAttemptOnlyAlertsTheVictimsOwnLog(string attempt)
    {
        using var service = new TriffAlertsService();
        var logs = new[]
        {
            (Listener: "Victim Pilot", Line: Attempt(attempt, Pilot("Hostile Pilot"), "you!")),
            (Listener: "Fleet Pilot", Line: Attempt(attempt, Pilot("Hostile Pilot"), Pilot("Victim Pilot"))),
            (Listener: "Hostile Pilot", Line: Attempt(attempt, "you", Pilot("Victim Pilot"))),
        };

        var alert = Assert.Single(logs.Select(log => service.ParseLine(log.Listener, log.Line))
            .OfType<TriffAlertEvent>());

        Assert.Equal("Victim Pilot", alert.CharacterName);
        Assert.Equal("warp_scramble", alert.Type);
        Assert.False(alert.Test);
    }

    [Theory]
    [InlineData("(combat) Warp scramble attempt from Enemy Pilot to you!")]
    [InlineData("(combat) Warp disruption attempt from Enemy Pilot to you.")]
    [InlineData("(combat) Warp scramble attempt from Enemy Pilot to you")]
    [InlineData("(COMBAT) WARP SCRAMBLE ATTEMPT FROM Enemy Pilot TO YOU!")]
    [InlineData("(combat) Warp scramble attempt from Guardian Initiate to you!")]
    [InlineData("(combat) <b>Warp scramble attempt</b> from <b>Enemy Pilot</b> to <b>you!</b>")]
    [InlineData("(combat) Warp disruption attempt from Enemy Pilot to&nbsp;you!")]
    [InlineData("(notify) You are within a warp disruption zone. Get 20000.0 meters from Warp Disrupt Probe to warp.")]
    [InlineData("(notify) <b>You are within a warp disruption zone.</b> Get 16000.0 meters from Warp Disrupt Probe to warp.")]
    public void PersonalAttemptsAndBubbleWarningsStillAlert(string message)
    {
        using var service = new TriffAlertsService();

        var alert = service.ParseLine("Local Pilot", LogLine(message));

        Assert.NotNull(alert);
        Assert.Equal("warp_scramble", alert.Type);
        Assert.Equal("Local Pilot", alert.CharacterName);
    }

    [Theory]
    [InlineData("(combat) Warp scramble attempt from Enemy Pilot to Fleet Pilot")]
    [InlineData("(combat) Warp disruption attempt from you to Enemy Pilot")]
    [InlineData("(combat) Warp scramble attempt from Fly to you to Fleet Pilot")]
    [InlineData("(combat) Warp scramble attempt from Enemy Pilot to you again [CORP] Frigate")]
    [InlineData("(combat) Warp scramble attempt from Enemy Pilot")]
    [InlineData("(combat) Warp disruption attempt")]
    [InlineData("(chat) Warp scramble attempt from Enemy Pilot to you!")]
    [InlineData("(notify) Fleet Pilot is within a warp disruption zone.")]
    [InlineData("(notify) You deploy a warp disruption zone.")]
    [InlineData("(chat) You are within a warp disruption zone.")]
    public void FleetOutgoingAndUnrelatedMessagesDoNotAlert(string message)
    {
        using var service = new TriffAlertsService();

        Assert.Null(service.ParseLine("Local Pilot", LogLine(message)));
    }

    // Same nested formatting as saved EVE combat logs, with invented pilot names.
    private static string Attempt(string attempt, string source, string target) => LogLine(
        $"(combat) <color=0xffffffff><b>{attempt}</b> <color=0x77ffffff><font size=10>from</font> " +
        $"<color=0xffffffff><b>{source}</b> <color=0x77ffffff><font size=10>to <b><color=0xffffffff></font>{target}");

    private static string Pilot(string name) =>
        $"<font size=12><color=0xFFFFFFFF><b>{name}</b> </color></font>" +
        "<font size=12><color=0xFFFFB300>[CORP]</color></font><font size=12>[FLEET]</font> " +
        "<font size=12><color=0xFFFFFFFF><b>Frigate</b></color></font>";

    private static string LogLine(string message) => $"[ 2026.09.17 12:00:00 ] {message}";
}
