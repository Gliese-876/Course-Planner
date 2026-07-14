using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CoursePlanner.Core;

public static partial class TextRules
{
    public static string TruncateUtf16(string? value, int maximumLength)
    {
        if (maximumLength < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumLength));

        value = SanitizeUtf16(value);
        if (value.Length <= maximumLength)
            return value;
        if (maximumLength == 0)
            return "";

        var length = maximumLength;
        if (char.IsHighSurrogate(value[length - 1]) && char.IsLowSurrogate(value[length]))
            length--;
        return value[..length];
    }

    public static string NormalizeIdentityText(string? value)
    {
        var normalized = SanitizeUtf16(value).Trim().Normalize(NormalizationForm.FormC);
        return WhitespaceRegex().Replace(normalized, " ");
    }

    public static string SanitizeUtf16(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value ?? "";

        var firstInvalid = -1;
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
                {
                    index++;
                    continue;
                }

                firstInvalid = index;
                break;
            }

            if (char.IsLowSurrogate(value[index]))
            {
                firstInvalid = index;
                break;
            }
        }

        if (firstInvalid < 0)
            return value;

        var sanitized = new StringBuilder(value.Length);
        sanitized.Append(value.AsSpan(0, firstInvalid));
        for (var index = firstInvalid; index < value.Length; index++)
        {
            var current = value[index];
            if (char.IsHighSurrogate(current))
            {
                if (index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
                {
                    sanitized.Append(current);
                    sanitized.Append(value[++index]);
                }
                else
                {
                    sanitized.Append('\uFFFD');
                }
            }
            else if (char.IsLowSurrogate(current))
            {
                sanitized.Append('\uFFFD');
            }
            else
            {
                sanitized.Append(current);
            }
        }

        return sanitized.ToString();
    }

    public static bool IsSameIdentityText(string? left, string? right) =>
        string.Equals(NormalizeIdentityText(left), NormalizeIdentityText(right), StringComparison.OrdinalIgnoreCase);

    public static bool IsSameLabel(string? left, string? right) =>
        IsSameIdentityText(left, right);

    public static IReadOnlyList<string> WrapTextWithAsciiHyphenation(string? value, int maxLines, Func<string, bool> fitsLine)
    {
        ArgumentNullException.ThrowIfNull(fitsLine);
        var sanitized = SanitizeUtf16(value);
        if (maxLines <= 0 || string.IsNullOrWhiteSpace(sanitized))
            return Array.Empty<string>();

        var remaining = WhitespaceRegex().Replace(sanitized.Trim(), " ");
        var lines = new List<string>();
        while (remaining.Length > 0 && lines.Count < maxLines)
        {
            if (fitsLine(remaining))
            {
                lines.Add(remaining);
                break;
            }

            if (lines.Count == maxLines - 1)
            {
                var fitted = FitTruncatedLine(remaining, fitsLine);
                if (fitted.Length > 0)
                    lines.Add(fitted);
                break;
            }

            var lineBreak = FindTextWrapBreak(remaining, fitsLine);
            if (lineBreak.TakeLength <= 0)
            {
                lines.Add(remaining);
                break;
            }

            lines.Add(remaining[..lineBreak.TakeLength].TrimEnd() + (lineBreak.Hyphenate ? "-" : ""));
            remaining = remaining[lineBreak.ConsumeLength..].TrimStart();
        }

        return lines;
    }

    private static string FitTruncatedLine(string text, Func<string, bool> fitsLine)
    {
        const string ellipsis = "…";
        var elementStarts = StringInfo.ParseCombiningCharacters(text);
        if (elementStarts.Length == 0)
            return "";

        if (fitsLine(ellipsis))
        {
            var low = 0;
            var high = elementStarts.Length;
            while (low < high)
            {
                var middle = low + ((high - low + 1) / 2);
                var end = middle >= elementStarts.Length ? text.Length : elementStarts[middle];
                if (fitsLine(text[..end] + ellipsis))
                    low = middle;
                else
                    high = middle - 1;
            }

            var length = low >= elementStarts.Length ? text.Length : elementStarts[low];
            return text[..length] + ellipsis;
        }

        var elementEnds = new int[elementStarts.Length];
        for (var index = 0; index < elementStarts.Length; index++)
            elementEnds[index] = index + 1 < elementStarts.Length ? elementStarts[index + 1] : text.Length;
        var largestFittingElement = FindLargestFittingElement(text, elementEnds, fitsLine);
        return largestFittingElement < 0 ? "" : text[..elementEnds[largestFittingElement]];
    }

    private static bool IsAsciiLetterOrDigit(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';

    private static TextWrapBreak FindTextWrapBreak(string text, Func<string, bool> fitsLine)
    {
        var elementStarts = StringInfo.ParseCombiningCharacters(text);
        if (elementStarts.Length == 0)
            return default;

        var elementEnds = new int[elementStarts.Length];
        for (var index = 0; index < elementStarts.Length; index++)
            elementEnds[index] = index + 1 < elementStarts.Length ? elementStarts[index + 1] : text.Length;

        var largestFittingElement = FindLargestFittingElement(text, elementEnds, fitsLine);
        if (largestFittingElement < 0)
        {
            // A text element is indivisible. It must advance as a unit even when the
            // renderer reports that the element itself is wider than the line.
            return new TextWrapBreak(elementEnds[0], elementEnds[0], false);
        }

        var largestFittingEnd = elementEnds[largestFittingElement];
        var naturalEnd = 0;
        for (var index = largestFittingElement; index >= 0; index--)
        {
            var candidateEnd = elementEnds[index];
            if (!IsNaturalTextBreak(text, candidateEnd))
                continue;
            naturalEnd = candidateEnd;
            break;
        }

        var hyphenEnd = FindLargestFittingAsciiHyphenBreak(
            text,
            naturalEnd,
            largestFittingEnd,
            fitsLine);
        if (hyphenEnd > naturalEnd)
            return new TextWrapBreak(hyphenEnd, hyphenEnd, true);
        if (naturalEnd > 0)
            return new TextWrapBreak(naturalEnd, ConsumeTrailingSpaces(text, naturalEnd), false);

        return new TextWrapBreak(elementEnds[0], elementEnds[0], false);
    }

    private static int FindLargestFittingElement(
        string text,
        IReadOnlyList<int> elementEnds,
        Func<string, bool> fitsLine)
    {
        var low = 0;
        var high = elementEnds.Count - 1;
        var best = -1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var candidate = text[..elementEnds[middle]].TrimEnd();
            if (candidate.Length > 0 && fitsLine(candidate))
            {
                best = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return best;
    }

    private static int FindLargestFittingAsciiHyphenBreak(
        string text,
        int naturalEnd,
        int largestFittingEnd,
        Func<string, bool> fitsLine)
    {
        if (largestFittingEnd <= naturalEnd ||
            largestFittingEnd >= text.Length ||
            !IsAsciiLetterOrDigit(text[largestFittingEnd - 1]) ||
            !IsAsciiLetterOrDigit(text[largestFittingEnd]))
        {
            return 0;
        }

        var wordStart = largestFittingEnd - 1;
        while (wordStart > 0 && IsAsciiLetterOrDigit(text[wordStart - 1]))
            wordStart--;

        var wordEnd = largestFittingEnd;
        while (wordEnd < text.Length && IsAsciiLetterOrDigit(text[wordEnd]))
            wordEnd++;

        var low = Math.Max(Math.Max(wordStart + 2, naturalEnd + 1), 1);
        var high = Math.Min(largestFittingEnd, wordEnd - 2);
        var best = 0;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (fitsLine(text[..middle] + "-"))
            {
                best = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return best;
    }

    private static bool IsNaturalTextBreak(string text, int index)
    {
        if (index <= 0 || index >= text.Length)
            return false;

        var previous = text[index - 1];
        var next = text[index];
        if (char.IsWhiteSpace(previous) || char.IsWhiteSpace(next))
            return true;
        if (previous is '-' or '/' or '\\' or ',' or ';' or ':' or '.')
            return true;

        return !IsAsciiLetterOrDigit(previous) || !IsAsciiLetterOrDigit(next);
    }

    private static int ConsumeTrailingSpaces(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;
        return index;
    }

    private readonly record struct TextWrapBreak(int TakeLength, int ConsumeLength, bool Hyphenate);

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
