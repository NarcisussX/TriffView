using System.Text;
using TriffView.Eve;
using TriffView.TriffFleets;
using TriffView.TriffSkills;
using Xunit;

namespace TriffView.Tests;

public class OAuthLoopbackTests
{
    private static readonly Uri Redirect = new("http://127.0.0.1:51777/trifffleets/callback/");

    [Fact]
    public void PkceValuesAreUniqueAndS256Compatible()
    {
        var first = PkceValues.Create();
        var second = PkceValues.Create();
        Assert.NotEqual(first.State, second.State);
        Assert.NotEqual(first.Verifier, second.Verifier);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", first.State);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", first.Verifier);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", first.Challenge);
    }

    [Fact]
    public async Task ParserAcceptsOnlyExpectedGetPathAndBoundsHeaders()
    {
        var valid = await Parse("GET /trifffleets/callback/?code=abc&state=xyz HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
        Assert.NotNull(valid);
        Assert.Equal("abc", valid!.Value.Query["code"]);

        Assert.Null(await Parse("POST /trifffleets/callback/?code=abc HTTP/1.1\r\n\r\n"));
        Assert.Null(await Parse("GET /wrong/?code=abc HTTP/1.1\r\n\r\n"));
        await Assert.ThrowsAsync<InvalidDataException>(() => Parse("GET /trifffleets/callback/?state=a&state=b HTTP/1.1\r\n\r\n"));
        await Assert.ThrowsAsync<InvalidDataException>(() => Parse($"GET /trifffleets/callback/ HTTP/1.1\r\nX: {new string('a', 33 * 1024)}\r\n\r\n"));
        await Assert.ThrowsAsync<InvalidDataException>(() => Parse($"GET /trifffleets/callback/?code={new string('a', 8_193)} HTTP/1.1\r\n\r\n"));
    }

    [Fact]
    public void CredentialNamespacesCannotCollide()
    {
        Assert.NotEqual(TriffSkillsController.CredentialPrefix, TriffFleetsController.CredentialPrefix);
        Assert.DoesNotContain(TriffSkillsController.CredentialPrefix, TriffFleetsController.CredentialPrefix, StringComparison.Ordinal);
        Assert.DoesNotContain(TriffFleetsController.CredentialPrefix, TriffSkillsController.CredentialPrefix, StringComparison.Ordinal);
    }

    [Fact]
    public void CredentialStoreRejectsInvalidTargetsAndSecretsBeforeNativeCalls()
    {
        var store = new WindowsCredentialStore();
        Assert.Throws<ArgumentException>(() => store.Read(""));
        Assert.Throws<ArgumentException>(() => store.Delete("bad\0target"));
        Assert.Throws<ArgumentException>(() => store.Write("test", ""));
        Assert.Throws<ArgumentException>(() => store.Write("test", "secret\0suffix"));
        Assert.Throws<ArgumentException>(() => store.Write("test", new string('x', 3_000)));
    }

    private static async Task<(string Method, string Path, Dictionary<string, string> Query)?> Parse(string request)
    {
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes(request));
        return await EveSsoClient.ParseCallbackRequestAsync(stream, Redirect, CancellationToken.None);
    }
}
