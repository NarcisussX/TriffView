using System.Collections.Concurrent;
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using TriffView.Alerts;

var tests = new (string Name, Action Body)[]
{
    ("Existing logs start at EOF", ExistingLogsStartAtEof),
    ("New live logs keep their first event", NewLiveLogsKeepFirstEvent),
    ("Newest character session wins", NewestCharacterSessionWins),
    ("Twenty-three clients dispatch without loss", MultiClientFanOut),
    ("Tab-sized preview dimensions persist safely", TabSizedPreviewDimensionsPersistSafely),
};

var failures = new List<string>();
foreach (var (name, body) in tests)
{
    try
    {
        body();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{name}: {ex.Message}");
        Console.Error.WriteLine($"FAIL {name}: {ex.Message}");
    }
}

if (failures.Count == 0) return 0;
Console.Error.WriteLine($"{failures.Count} TriffAlerts regression test(s) failed.");
return 1;

static void ExistingLogsStartAtEof()
{
    WithService((directory, service, alerts) =>
    {
        var path = Path.Combine(directory, "20260711_100000_1001.txt");
        File.WriteAllText(path, Header("Existing Pilot") + FleetInvite("Old Invite"), Encoding.UTF8);
        Enable(service);

        Thread.Sleep(300);
        Require(alerts.IsEmpty, "An event already present at startup was replayed.");

        File.AppendAllText(path, FleetInvite("Live Invite"), Encoding.UTF8);
        Require(WaitFor(() => alerts.Count == 1), "A live append to an existing session was not detected.");
        Require(alerts.Single().CharacterName == "Existing Pilot", "The existing session was mapped to the wrong character.");
    });
}

static void NewLiveLogsKeepFirstEvent()
{
    WithService((directory, service, alerts) =>
    {
        Enable(service);
        var path = Path.Combine(directory, "20260711_110000_1002.txt");
        File.WriteAllText(path, Header("Fresh Pilot") + FleetInvite("Immediate Invite"), Encoding.UTF8);

        Require(WaitFor(() => alerts.Count == 1), "The first event written with a newly created log was dropped.");
        Require(alerts.Single().CharacterName == "Fresh Pilot", "The new session was mapped to the wrong character.");
    });
}

static void NewestCharacterSessionWins()
{
    WithService((directory, service, alerts) =>
    {
        var oldPath = Path.Combine(directory, "20260711_090000_1003.txt");
        File.WriteAllText(oldPath, Header("Session Pilot"), Encoding.UTF8);
        Thread.Sleep(40);
        var newPath = Path.Combine(directory, "20260711_120000_1003.txt");
        File.WriteAllText(newPath, Header("Session Pilot"), Encoding.UTF8);
        Enable(service);

        File.AppendAllText(oldPath, FleetInvite("Stale Invite"), Encoding.UTF8);
        Thread.Sleep(300);
        Require(alerts.IsEmpty, "An older session displaced the current character log.");

        File.AppendAllText(newPath, FleetInvite("Current Invite"), Encoding.UTF8);
        Require(WaitFor(() => alerts.Count == 1), "The newest character session was not monitored.");
    });
}

static void MultiClientFanOut()
{
    WithService((directory, service, alerts) =>
    {
        const int clientCount = 23;
        var paths = new List<string>();
        for (var index = 0; index < clientCount; index++)
        {
            var path = Path.Combine(directory, $"20260711_13{index:00}00_{2000 + index}.txt");
            File.WriteAllText(path, Header($"Pilot {index + 1}"), Encoding.UTF8);
            paths.Add(path);
        }

        Enable(service);
        var stopwatch = Stopwatch.StartNew();
        Parallel.ForEach(paths, path => File.AppendAllText(path, FleetInvite("Fleet Boss"), Encoding.UTF8));

        Require(WaitFor(() => alerts.Count == clientCount, timeoutMs: 2000), $"Only {alerts.Count} of {clientCount} alerts arrived within two seconds.");
        stopwatch.Stop();
        Require(alerts.Select(alert => alert.CharacterName).Distinct(StringComparer.OrdinalIgnoreCase).Count() == clientCount, "At least one client alert was duplicated while another was lost.");
        Console.WriteLine($"     fan-out latency: {stopwatch.ElapsedMilliseconds} ms");
    });
}

static void TabSizedPreviewDimensionsPersistSafely()
{
    var assembly = typeof(TriffAlertsService).Assembly;
    var settingsType = assembly.GetType("TriffView.Preview.TriffViewSettings", throwOnError: true)!;
    var fromJson = settingsType.GetMethod("FromJson", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("TriffView settings parser was not found.");
    var json = """
        {
          "profiles": [
            {
              "id": "tiny",
              "name": "Tiny",
              "previewWidth": 1,
              "previewHeight": 7,
              "previewLayouts": {
                "Pilot": { "x": 1, "y": 2, "width": 2, "height": 3 }
              }
            },
            {
              "id": "large",
              "name": "Large",
              "previewWidth": 50000,
              "previewHeight": 50000
            }
          ],
          "selectedProfileId": "tiny"
        }
        """;
    var settings = fromJson.Invoke(null, new object[] { json })
        ?? throw new InvalidOperationException("TriffView settings parser returned no settings.");
    var profiles = (IEnumerable)(settingsType.GetProperty("Profiles")?.GetValue(settings)
        ?? throw new InvalidOperationException("TriffView profiles were not found."));

    foreach (var profile in profiles)
    {
        var profileType = profile.GetType();
        var name = (string?)profileType.GetProperty("Name")?.GetValue(profile);
        var width = (int)(profileType.GetProperty("PreviewWidth")?.GetValue(profile) ?? 0);
        var height = (int)(profileType.GetProperty("PreviewHeight")?.GetValue(profile) ?? 0);
        if (name == "Tiny")
        {
            Require(width == 16 && height == 16, $"Tiny defaults normalized to {width}x{height} instead of 16x16.");
            var layouts = (IDictionary)(profileType.GetProperty("PreviewLayouts")?.GetValue(profile)
                ?? throw new InvalidOperationException("Tiny preview layouts were not found."));
            var layout = layouts["Pilot"] ?? throw new InvalidOperationException("Tiny saved layout was not preserved.");
            var layoutType = layout.GetType();
            var layoutWidth = (int)(layoutType.GetProperty("Width")?.GetValue(layout) ?? 0);
            var layoutHeight = (int)(layoutType.GetProperty("Height")?.GetValue(layout) ?? 0);
            Require(layoutWidth == 16 && layoutHeight == 16, $"Tiny saved layout normalized to {layoutWidth}x{layoutHeight} instead of 16x16.");
        }
        else if (name == "Large")
        {
            Require(width == 32767 && height == 32767, $"Large dimensions bypassed the Win32 safety guard: {width}x{height}.");
        }
    }
}

static void WithService(Action<string, TriffAlertsService, ConcurrentQueue<TriffAlertEvent>> body)
{
    var directory = Path.Combine(Path.GetTempPath(), "TriffAlerts.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        using var service = new TriffAlertsService(directory);
        var alerts = new ConcurrentQueue<TriffAlertEvent>();
        service.AlertTriggered += (_, alert) => alerts.Enqueue(alert);
        body(directory, service, alerts);
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void Enable(TriffAlertsService service)
{
    var settings = TriffAlertsSettings.CreateDefault();
    settings.Enabled = true;
    settings.PveMode = false;
    settings.Event("fleet_invite").CooldownSeconds = 0;
    service.UpdateSettings(settings);
}

static bool WaitFor(Func<bool> condition, int timeoutMs = 1500)
{
    return SpinWait.SpinUntil(condition, TimeSpan.FromMilliseconds(timeoutMs));
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static string Header(string characterName) =>
    $"------------------------------------------------------------\r\n  Gamelog\r\n  Listener: {characterName}\r\n  Session started: 2026.07.11 10:00:00\r\n------------------------------------------------------------\r\n";

static string FleetInvite(string source) =>
    $"[ 2026.07.11 10:00:01 ] (question) <a href=\"showinfo:1373//1\">{source}</a> wants you to join their fleet\r\n";
