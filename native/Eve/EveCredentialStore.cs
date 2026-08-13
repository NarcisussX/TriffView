using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace TriffView.Eve;

internal interface ICredentialStore
{
    string? Read(string target);
    void Write(string target, string secret);
    void Delete(string target, bool missingIsSuccess = true);
    IReadOnlyList<string> EnumerateTargets(string exactPrefix);
}

internal sealed class WindowsCredentialStore : ICredentialStore
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public string? Read(string target)
    {
        ValidateTarget(target);
        if (!CredRead(target, CredTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return null;
            throw Win32Failure("read", target, error);
        }

        if (pointer == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Windows Credential Manager returned an empty pointer while reading '{target}'.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return string.Empty;
            if (credential.CredentialBlobSize > 10_240 || credential.CredentialBlobSize % 2 != 0)
            {
                throw new InvalidDataException($"Windows Credential Manager returned an invalid secret size for '{target}'.");
            }

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public void Write(string target, string secret)
    {
        ValidateTarget(target);
        if (string.IsNullOrWhiteSpace(secret) || secret.IndexOf('\0') >= 0) throw new ArgumentException("Credential secret is invalid.", nameof(secret));

        var bytes = Encoding.Unicode.GetBytes(secret);
        if (bytes.Length > 5_120) throw new ArgumentException("Credential secret is too large.", nameof(secret));

        var blob = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = "TriffView",
            };

            if (!CredWrite(ref credential, 0))
            {
                throw Win32Failure("write", target, Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(blob);
        }
    }

    public void Delete(string target, bool missingIsSuccess = true)
    {
        ValidateTarget(target);
        if (CredDelete(target, CredTypeGeneric, 0)) return;

        var error = Marshal.GetLastWin32Error();
        if (missingIsSuccess && error == ErrorNotFound) return;
        throw Win32Failure("delete", target, error);
    }

    public IReadOnlyList<string> EnumerateTargets(string exactPrefix)
    {
        ValidateTarget(exactPrefix);
        var filter = exactPrefix + "*";
        if (!CredEnumerate(filter, 0, out var count, out var credentials))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return Array.Empty<string>();
            throw Win32Failure("enumerate", exactPrefix, error);
        }

        if (credentials == IntPtr.Zero) return Array.Empty<string>();
        try
        {
            var targets = new List<string>((int)Math.Min(count, 4096));
            for (var index = 0; index < count && index < 4096; index++)
            {
                var credentialPointer = Marshal.ReadIntPtr(credentials, checked((int)index * IntPtr.Size));
                if (credentialPointer == IntPtr.Zero) continue;
                var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
                var target = credential.TargetName ?? string.Empty;
                if (target.StartsWith(exactPrefix, StringComparison.Ordinal) && target.Length > exactPrefix.Length)
                {
                    targets.Add(target);
                }
            }
            return targets;
        }
        finally
        {
            CredFree(credentials);
        }
    }

    private static void ValidateTarget(string target)
    {
        if (string.IsNullOrWhiteSpace(target) || target.Length > 256 || target.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("Credential target is invalid.", nameof(target));
        }
    }

    private static InvalidOperationException Win32Failure(string operation, string target, int error)
        => new($"Windows Credential Manager {operation} failed for '{target}' (Win32 {error}).");

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredEnumerate(string filter, uint flags, out uint count, out IntPtr credentials);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }
}
