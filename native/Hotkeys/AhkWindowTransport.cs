using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Forms = System.Windows.Forms;

namespace TriffView.Preview;

internal sealed class AhkWindowTransport : Forms.NativeWindow, IAhkTransport
{
    internal const int ReadyMessage = 0x8051;
    internal const int PressedMessage = 0x8052;
    internal const int ActivateMessage = 0x8053;
    internal const int StopMessage = 0x8054;
    private const int CopyData = 0x004A;
    private const nuint Protocol = 0x54564132; // TVA2
    private readonly string? _runtimeDirectory;
    private Process? _process;
    private nint _helperWindow;
    private long _startedAt;
    private long _retryAfter;
    private bool _disposed;
    private readonly StringBuilder _errors = new();
    public bool IsReady => _helperWindow != 0 && _process is { HasExited: false };
    public string? Failure { get; private set; }
    internal int? ProcessId => _process is { HasExited: false } ? _process.Id : null;
    public event Action? Ready;
    public event Action<int, int>? Pressed;

    public AhkWindowTransport(string? runtimeDirectory = null) => _runtimeDirectory = runtimeDirectory;

    public void EnsureRunning()
    {
        if (_disposed) return;
        if (_process != null)
        {
            if (!_process.HasExited && (_helperWindow != 0 || Environment.TickCount64 - _startedAt < 5000)) return;
            lock (_errors) Failure = $"AutoHotkey stopped or did not start: {_errors.ToString().Trim()}";
            StopProcess();
            _retryAfter = Environment.TickCount64 + 5000;
        }
        if (Environment.TickCount64 < _retryAfter) return;
        try
        {
            if (Handle == 0) CreateHandle(new Forms.CreateParams { Caption = "TriffView hotkey IPC", Parent = new nint(-3) });
            var directory = _runtimeDirectory ?? AhkRuntime.Extract();
            var start = new ProcessStartInfo(Path.Combine(directory, "AutoHotkey64.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                WorkingDirectory = directory,
            };
            start.ArgumentList.Add("/ErrorStdOut=UTF-8");
            start.ArgumentList.Add(Path.Combine(directory, "TriffViewHotkeys.ahk"));
            start.ArgumentList.Add(Handle.ToInt64().ToString());
            start.ArgumentList.Add(Environment.ProcessId.ToString());
            lock (_errors) _errors.Clear();
            _process = new Process { StartInfo = start };
            _process.ErrorDataReceived += CaptureError;
            _process.OutputDataReceived += CaptureError;
            _process.Start();
            _startedAt = Environment.TickCount64;
            _process.BeginErrorReadLine();
            _process.BeginOutputReadLine();
            Failure = "AutoHotkey is starting";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Failure = $"AutoHotkey could not start: {ex.Message}";
            StopProcess();
            _retryAfter = Environment.TickCount64 + 5000;
        }
    }

    private void CaptureError(object sender, DataReceivedEventArgs args)
    {
        if (args.Data == null) return;
        lock (_errors) if (_errors.Length < 4096) _errors.AppendLine(args.Data);
    }

    protected override void WndProc(ref Forms.Message message)
    {
        if (message.Msg == ReadyMessage && !_disposed && _process is { HasExited: false })
        {
            TriffViewNativeMethods.GetWindowThreadProcessId(message.WParam, out var pid);
            if (pid == _process.Id && message.LParam == 2)
            {
                _helperWindow = message.WParam;
                Failure = null;
                Ready?.Invoke();
            }
            return;
        }
        if (message.Msg == PressedMessage)
        {
            if (IsReady && !_disposed) Pressed?.Invoke(message.WParam.ToInt32(), message.LParam.ToInt32());
            return;
        }
        base.WndProc(ref message);
    }

    public bool Send(string message)
    {
        if (!IsReady) return false;
        var data = Marshal.StringToHGlobalUni(message);
        var packet = new CopyDataPacket { Tag = Protocol, Size = checked((uint)((message.Length + 1) * 2)), Data = data };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<CopyDataPacket>());
        try
        {
            Marshal.StructureToPtr(packet, pointer, false);
            return Request(CopyData, Handle, pointer);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
            Marshal.FreeHGlobal(data);
        }
    }

    public bool Activate(nint handle, bool maximize) => IsReady && Request(ActivateMessage, handle, maximize ? 1 : 0);

    private bool Request(int message, nint wParam, nint lParam)
    {
        // Block reentrant hotkey dispatch while a switch completes, preserving cycle order.
        if (SendMessageTimeout(_helperWindow, message, wParam, lParam, 0x0001 | 0x0002, 1500, out var result) != 0)
            return result == 1;
        Failure = "AutoHotkey did not respond; restarting hotkeys";
        StopProcess(); // A timed-out activation must never execute later against a stale target.
        _retryAfter = Environment.TickCount64 + 1000;
        return false;
    }

    private void StopProcess()
    {
        _helperWindow = 0;
        var process = _process;
        _process = null;
        if (process == null) return;
        try
        {
            if (!process.HasExited) process.Kill();
            process.WaitForExit(1000);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        finally { process.Dispose(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (IsReady)
        {
            PostMessage(_helperWindow, StopMessage, Handle, 0);
            _process?.WaitForExit(1000);
        }
        StopProcess();
        if (Handle != 0) DestroyHandle();
        GC.SuppressFinalize(this);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyDataPacket { public nuint Tag; public uint Size; public nint Data; }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SendMessageTimeout(nint hwnd, int message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint hwnd, int message, nint wParam, nint lParam);
}

internal static class AhkRuntime
{
    // Embedded resources also cover single-file releases. No installed AHK or network download is used.
    internal static string Extract(string? root = null)
    {
        var assembly = typeof(AhkRuntime).Assembly;
        var names = new[] { "AutoHotkey64.exe", "TriffViewHotkeys.ahk", "license.txt", "AutoHotkey-source.zip", "EVE-X-LICENSE.txt" };
        var resources = names.Select(name =>
        {
            using var stream = assembly.GetManifestResourceStream("TriffView.Hotkeys." + name)
                ?? throw new IOException($"Missing bundled hotkey component: {name}");
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return (Name: name, Bytes: buffer.ToArray());
        }).ToArray();
        var bundleHash = Convert.ToHexString(SHA256.HashData(resources.SelectMany(file => SHA256.HashData(file.Bytes)).ToArray()));
        var directory = Path.Combine(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TriffView", "Hotkeys"), bundleHash[..20]);
        Directory.CreateDirectory(directory);
        foreach (var file in resources)
        {
            var path = Path.Combine(directory, file.Name);
            if (File.Exists(path) && SHA256.HashData(File.ReadAllBytes(path)).SequenceEqual(SHA256.HashData(file.Bytes))) continue;
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllBytes(temporary, file.Bytes); File.Move(temporary, path, overwrite: true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return directory;
    }
}
