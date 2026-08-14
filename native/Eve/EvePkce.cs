using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace TriffView.Eve;

internal sealed record PkceValues(string State, string Verifier, string Challenge)
{
    public static PkceValues Create()
    {
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return new PkceValues(state, verifier, challenge);
    }

    internal static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

internal interface IBrowserLauncher
{
    void Launch(Uri uri);
}

internal sealed class SystemBrowserLauncher : IBrowserLauncher
{
    public void Launch(Uri uri) => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
}
