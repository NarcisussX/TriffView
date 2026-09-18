using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using TriffView.Preview;

namespace TriffView.Tests;

// These tests never run the hotkey script: /validate exits before AutoExecute.
// Keep live hotkey registration and foreground/input tests out of the local suite.
public class AhkRuntimeTests
{
    [Fact]
    public async Task BundledRuntimeValidatesScriptWithoutExecutingIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "triffview-ahk-validation", Guid.NewGuid().ToString("N"));
        try
        {
            var directory = AhkRuntime.Extract(root);
            var start = new ProcessStartInfo(Path.Combine(directory, "AutoHotkey64.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardError = true, RedirectStandardOutput = true,
            };
            start.ArgumentList.Add("/validate");
            start.ArgumentList.Add("/ErrorStdOut=UTF-8");
            start.ArgumentList.Add(Path.Combine(directory, "TriffViewHotkeys.ahk"));
            using var process = Process.Start(start)!;
            try
            {
                var error = process.StandardError.ReadToEndAsync();
                var output = process.StandardOutput.ReadToEndAsync();
                Assert.True(process.WaitForExit(5000), "AHK syntax validation timed out");
                Assert.True(process.ExitCode == 0, await output + await error);
            }
            finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(1000); } }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void BundleIncludesMatchingSourceAndNoticesAndRepairsAlteredExtractedFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "triffview-ahk-extraction", Guid.NewGuid().ToString("N"));
        try
        {
            var directory = AhkRuntime.Extract(root);
            Assert.Equal("373181727D1AE858564D4DAA678F9FA6CF330D1751F8B284A369C79AFDB05E98",
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, "AutoHotkey64.exe")))));
            Assert.Contains("Version 2, June 1991", File.ReadAllText(Path.Combine(directory, "license.txt")));
            Assert.Contains("g0nzo83", File.ReadAllText(Path.Combine(directory, "EVE-X-LICENSE.txt")));
            using (var source = ZipFile.OpenRead(Path.Combine(directory, "AutoHotkey-source.zip")))
                Assert.Contains(source.Entries, entry => entry.FullName.EndsWith("source/AutoHotkey.cpp"));
            var script = Path.Combine(directory, "TriffViewHotkeys.ahk");
            var expected = File.ReadAllText(script);
            File.WriteAllText(script, "invalid");
            Assert.Equal(directory, AhkRuntime.Extract(root));
            Assert.Equal(expected, File.ReadAllText(script));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void MissingRuntimeIsReportedWithoutStartingAProcess() => RunSta(() =>
    {
        using var transport = new AhkWindowTransport(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        transport.EnsureRunning();
        Assert.False(transport.IsReady);
        Assert.Contains("could not start", transport.Failure);
        transport.Dispose();
        transport.EnsureRunning();
        Assert.Null(transport.ProcessId);
    });

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "STA test timed out");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
