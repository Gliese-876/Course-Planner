using System.Buffers;
using System.Text;
using System.Text.Json;

namespace CoursePlanner.Core;

public sealed class DuplicateJsonPropertyException : JsonException
{
    public DuplicateJsonPropertyException(string propertyName)
        : base($"The JSON object contains the duplicate property '{propertyName}'.")
    {
        PropertyName = propertyName;
    }

    public string PropertyName { get; }
}

public static class JsonInputGuard
{
    public const int MaximumTokenCount = 5_000_000;
    public const int MaximumPropertiesPerObject = 64;
    public const int MaximumItemsPerArray = PlannerDataLimits.MaxCourses;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static void Validate(string json, int maximumDepth = 64) =>
        _ = ValidateCore(json, rootStringProperty: null, maximumDepth);

    public static string? ReadRootStringProperty(
        string json,
        string propertyName,
        int maximumDepth = 64)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        return ValidateCore(json, propertyName, maximumDepth);
    }

    public static JsonDocument ParseDocument(string json, int maximumDepth = 64)
    {
        Validate(json, maximumDepth);
        return JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = maximumDepth });
    }

    private static string? ValidateCore(
        string json,
        string? rootStringProperty,
        int maximumDepth)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (maximumDepth <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumDepth));

        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(json);
        }
        catch (EncoderFallbackException exception)
        {
            throw new JsonException("The JSON text contains an unpaired UTF-16 surrogate.", exception);
        }

        var rented = ArrayPool<byte>.Shared.Rent(Math.Max(1, byteCount));
        try
        {
            StrictUtf8.GetBytes(json.AsSpan(), rented.AsSpan(0, byteCount));
            var reader = new Utf8JsonReader(
                rented.AsSpan(0, byteCount),
                new JsonReaderOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = maximumDepth
                });
            var frames = new Stack<ContainerFrame>();
            var tokenCount = 0;
            var captureNextRootValue = false;
            string? rootValue = null;

            while (reader.Read())
            {
                tokenCount++;
                if (tokenCount > MaximumTokenCount)
                    throw new JsonException($"JSON exceeds the token limit of {MaximumTokenCount}.");

                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        IncrementArrayItem(frames);
                        captureNextRootValue = false;
                        frames.Push(ContainerFrame.Object());
                        break;
                    case JsonTokenType.StartArray:
                        IncrementArrayItem(frames);
                        captureNextRootValue = false;
                        frames.Push(ContainerFrame.Array());
                        break;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        if (frames.Count == 0)
                            throw new JsonException("JSON contains an unmatched container terminator.");
                        frames.Pop();
                        captureNextRootValue = false;
                        break;
                    case JsonTokenType.PropertyName:
                        {
                            if (frames.Count == 0 || !frames.Peek().IsObject)
                                throw new JsonException("JSON property is outside an object.");
                            var propertyName = reader.GetString() ?? "";
                            var frame = frames.Peek();
                            frame.PropertyCount++;
                            if (frame.PropertyCount > MaximumPropertiesPerObject)
                            {
                                throw new JsonException(
                                    $"JSON object exceeds the property limit of {MaximumPropertiesPerObject}.");
                            }
                            if (!frame.PropertyNames!.Add(propertyName))
                                throw new DuplicateJsonPropertyException(propertyName);
                            captureNextRootValue = frames.Count == 1 &&
                                                   rootStringProperty is not null &&
                                                   string.Equals(propertyName, rootStringProperty, StringComparison.Ordinal);
                            break;
                        }
                    default:
                        IncrementArrayItem(frames);
                        if (captureNextRootValue && reader.TokenType == JsonTokenType.String)
                            rootValue = reader.GetString();
                        captureNextRootValue = false;
                        break;
                }
            }

            if (frames.Count != 0)
                throw new JsonException("JSON contains an unterminated container.");
            return rootValue;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    private static void IncrementArrayItem(Stack<ContainerFrame> frames)
    {
        if (frames.Count == 0 || frames.Peek().IsObject)
            return;
        var frame = frames.Peek();
        frame.ItemCount++;
        if (frame.ItemCount > MaximumItemsPerArray)
        {
            throw new JsonException(
                $"JSON array exceeds the item limit of {MaximumItemsPerArray}.");
        }
    }

    private sealed class ContainerFrame
    {
        private ContainerFrame(bool isObject)
        {
            IsObject = isObject;
            PropertyNames = isObject ? new HashSet<string>(StringComparer.Ordinal) : null;
        }

        public bool IsObject { get; }
        public HashSet<string>? PropertyNames { get; }
        public int PropertyCount { get; set; }
        public int ItemCount { get; set; }

        public static ContainerFrame Object() => new(isObject: true);
        public static ContainerFrame Array() => new(isObject: false);
    }
}
