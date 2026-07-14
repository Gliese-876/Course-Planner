using System.Text;

namespace CoursePlanner.Services;

public sealed class TextFileLimitExceededException : IOException
{
    public TextFileLimitExceededException(long maximumBytes, int maximumCharacters)
        : base($"The text file exceeds the configured limit of {maximumBytes} bytes or {maximumCharacters} characters.")
    {
        MaximumBytes = maximumBytes;
        MaximumCharacters = maximumCharacters;
    }

    public long MaximumBytes { get; }
    public int MaximumCharacters { get; }
}

public static class BoundedTextFileReader
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly Encoding StrictUtf16LittleEndian = new UnicodeEncoding(
        bigEndian: false,
        byteOrderMark: false,
        throwOnInvalidBytes: true);
    private static readonly Encoding StrictUtf16BigEndian = new UnicodeEncoding(
        bigEndian: true,
        byteOrderMark: false,
        throwOnInvalidBytes: true);
    private static readonly Encoding StrictUtf32LittleEndian = new UTF32Encoding(
        bigEndian: false,
        byteOrderMark: false,
        throwOnInvalidCharacters: true);
    private static readonly Encoding StrictUtf32BigEndian = new UTF32Encoding(
        bigEndian: true,
        byteOrderMark: false,
        throwOnInvalidCharacters: true);

    public static async Task<string> ReadAsync(
        string path,
        long maximumBytes,
        int maximumCharacters,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (maximumCharacters <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumCharacters));

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > maximumBytes)
            throw new TextFileLimitExceededException(maximumBytes, maximumCharacters);

        var header = new byte[4];
        var headerLength = 0;
        while (headerLength < header.Length)
        {
            var read = await stream.ReadAsync(header.AsMemory(headerLength), cancellationToken);
            if (read == 0)
                break;
            headerLength += read;
        }

        var (encoding, preambleLength) = DetectEncoding(header.AsSpan(0, headerLength));
        stream.Position = preambleLength;

        using var reader = new StreamReader(
            stream,
            encoding,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 64 * 1024,
            leaveOpen: true);
        var result = new StringBuilder((int)Math.Min(maximumCharacters, stream.Length));
        var buffer = new char[16 * 1024];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
                break;
            if (stream.Position > maximumBytes || result.Length > maximumCharacters - read)
                throw new TextFileLimitExceededException(maximumBytes, maximumCharacters);
            result.Append(buffer, 0, read);
        }

        if (stream.Position > maximumBytes)
            throw new TextFileLimitExceededException(maximumBytes, maximumCharacters);
        return result.ToString();
    }

    private static (Encoding Encoding, int PreambleLength) DetectEncoding(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 4 &&
            header[0] == 0xFF && header[1] == 0xFE && header[2] == 0x00 && header[3] == 0x00)
        {
            return (StrictUtf32LittleEndian, 4);
        }

        if (header.Length >= 4 &&
            header[0] == 0x00 && header[1] == 0x00 && header[2] == 0xFE && header[3] == 0xFF)
        {
            return (StrictUtf32BigEndian, 4);
        }

        if (header.Length >= 3 &&
            header[0] == 0xEF && header[1] == 0xBB && header[2] == 0xBF)
        {
            return (StrictUtf8, 3);
        }

        if (header.Length >= 2 && header[0] == 0xFF && header[1] == 0xFE)
            return (StrictUtf16LittleEndian, 2);
        if (header.Length >= 2 && header[0] == 0xFE && header[1] == 0xFF)
            return (StrictUtf16BigEndian, 2);

        return (StrictUtf8, 0);
    }
}
