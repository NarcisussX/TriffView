using TriffView.Eve;
using Xunit;

namespace TriffView.Tests;

public class AtomicFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "triffview-atomic-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void BoundedReaderRejectsOversizeAndInvalidUtf8()
    {
        Directory.CreateDirectory(_dir);
        var oversize = Path.Combine(_dir, "oversize.txt");
        File.WriteAllBytes(oversize, new byte[17]);
        Assert.Throws<InvalidDataException>(() => AtomicFile.ReadBoundedText(oversize, 16));

        var invalid = Path.Combine(_dir, "invalid.txt");
        File.WriteAllBytes(invalid, [0xC3, 0x28]);
        Assert.Throws<DecoderFallbackException>(() => AtomicFile.ReadBoundedText(invalid, 16));
    }
}
