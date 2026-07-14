using System.Text;
using CoursePlanner.Services;

namespace CoursePlanner.Tests;

public sealed class BoundedTextFileReaderTests
{
    [Fact]
    public async Task ReadsUtf8AndBomEncodedTextWithinBothLimits()
    {
        using var workspace = new TestDirectory();
        var utf8Path = Path.Combine(workspace.Path, "utf8.txt");
        var utf16Path = Path.Combine(workspace.Path, "utf16.txt");
        await File.WriteAllTextAsync(utf8Path, "课程 😀", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await File.WriteAllTextAsync(utf16Path, "semester", Encoding.Unicode);

        Assert.Equal("课程 😀", await BoundedTextFileReader.ReadAsync(utf8Path, 100, 20));
        Assert.Equal("semester", await BoundedTextFileReader.ReadAsync(utf16Path, 100, 20));
    }

    [Theory]
    [MemberData(nameof(StrictUnicodeEncodings))]
    public async Task ReadsEverySupportedUnicodeBomWithoutReplacement(
        byte[] preamble,
        byte[] payload)
    {
        using var workspace = new TestDirectory();
        var path = Path.Combine(workspace.Path, "unicode.txt");
        await File.WriteAllBytesAsync(path, [.. preamble, .. payload]);

        Assert.Equal("课程 😀", await BoundedTextFileReader.ReadAsync(path, 100, 20));
    }

    [Theory]
    [InlineData(new byte[] { 0x7B, 0x22, 0x78, 0x22, 0x3A, 0x22, 0xC3, 0x28, 0x22, 0x7D })]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, 0xED, 0xA0, 0x80 })]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x00, 0xD8 })]
    [InlineData(new byte[] { 0xFE, 0xFF, 0xD8, 0x00 })]
    public async Task MalformedUnicodeIsRejectedInsteadOfSilentlyChanged(byte[] bytes)
    {
        using var workspace = new TestDirectory();
        var path = Path.Combine(workspace.Path, "malformed.txt");
        await File.WriteAllBytesAsync(path, bytes);

        await Assert.ThrowsAsync<DecoderFallbackException>(() =>
            BoundedTextFileReader.ReadAsync(path, 100, 100));
    }

    [Fact]
    public async Task RejectsInitialFileLengthAboveTheByteLimit()
    {
        using var workspace = new TestDirectory();
        var path = Path.Combine(workspace.Path, "bytes.txt");
        await File.WriteAllBytesAsync(path, new byte[101]);

        await Assert.ThrowsAsync<TextFileLimitExceededException>(() =>
            BoundedTextFileReader.ReadAsync(path, maximumBytes: 100, maximumCharacters: 100));
    }

    [Fact]
    public async Task RejectsDecodedTextAboveTheCharacterLimitWithoutReturningAPrefix()
    {
        using var workspace = new TestDirectory();
        var path = Path.Combine(workspace.Path, "characters.txt");
        await File.WriteAllTextAsync(path, new string('a', 101));

        await Assert.ThrowsAsync<TextFileLimitExceededException>(() =>
            BoundedTextFileReader.ReadAsync(path, maximumBytes: 1_000, maximumCharacters: 100));
    }

    [Fact]
    public async Task InvalidLimitsFailBeforeOpeningTheFile()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            BoundedTextFileReader.ReadAsync("missing", maximumBytes: 0, maximumCharacters: 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            BoundedTextFileReader.ReadAsync("missing", maximumBytes: 1, maximumCharacters: 0));
    }

    public static IEnumerable<object[]> StrictUnicodeEncodings()
    {
        const string value = "课程 😀";
        foreach (var encoding in new Encoding[]
                 {
                     new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true),
                     new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true),
                     new UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: true),
                     new UTF32Encoding(bigEndian: false, byteOrderMark: true, throwOnInvalidCharacters: true),
                     new UTF32Encoding(bigEndian: true, byteOrderMark: true, throwOnInvalidCharacters: true)
                 })
        {
            yield return [encoding.GetPreamble(), encoding.GetBytes(value)];
        }
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
