using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CoursePlanner.Core;

public static partial class WindowsFileNameRules
{
    public const int MaxComponentLength = 255;

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static ValidationResult ValidateFileComponent(string value)
    {
        var result = new ValidationResult();
        if (string.IsNullOrWhiteSpace(value))
        {
            result.Error("FileNameRequired");
            return result;
        }

        if (value.EndsWith(' ') || value.EndsWith('.'))
            result.Error("FileNameTrailing");
        if (IllegalCharsRegex().IsMatch(value) ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            value.Any(char.IsControl))
        {
            result.Error("FileNameIllegalCharacters");
        }
        if (value.Length > MaxComponentLength)
            result.Error("FileNameTooLong", MaxComponentLength.ToString(CultureInfo.InvariantCulture));

        var firstExtensionSeparator = value.IndexOf('.');
        var stem = firstExtensionSeparator < 0 ? value : value[..firstExtensionSeparator];
        if (Reserved.Contains(stem))
            result.Error("FileNameReserved");
        return result;
    }

    public static string CreateBoundedSuggestion(string preferredStem, string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(preferredStem);
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        if (extension[0] != '.' ||
            extension.Length >= MaxComponentLength ||
            !ValidateFileComponent("x" + extension).IsValid)
        {
            throw new ArgumentException("The file extension must be a valid Windows file-name extension.", nameof(extension));
        }

        if (preferredStem.Length + extension.Length <= MaxComponentLength)
            return preferredStem + extension;

        const string omissionMarker = "…";
        var stemBudget = MaxComponentLength - extension.Length;
        var contentBudget = stemBudget - omissionMarker.Length;
        if (contentBudget < 0)
            throw new ArgumentException("The file extension leaves no room for a file name.", nameof(extension));

        var elementStarts = StringInfo.ParseCombiningCharacters(preferredStem);
        if (elementStarts.Length == 0)
            return omissionMarker + extension;

        var elementEnds = new int[elementStarts.Length];
        for (var index = 0; index < elementStarts.Length; index++)
            elementEnds[index] = index + 1 < elementStarts.Length ? elementStarts[index + 1] : preferredStem.Length;

        var prefixCount = 0;
        var prefixLength = 0;
        var desiredPrefixLength = (contentBudget + 1) / 2;
        while (prefixCount < elementStarts.Length)
        {
            var elementLength = elementEnds[prefixCount] - elementStarts[prefixCount];
            if (prefixLength + elementLength > desiredPrefixLength)
                break;
            prefixLength += elementLength;
            prefixCount++;
        }

        var suffixStartElement = elementStarts.Length;
        var suffixLength = 0;
        var desiredSuffixLength = contentBudget - prefixLength;
        while (suffixStartElement > prefixCount)
        {
            var candidate = suffixStartElement - 1;
            var elementLength = elementEnds[candidate] - elementStarts[candidate];
            if (suffixLength + elementLength > desiredSuffixLength)
                break;
            suffixStartElement = candidate;
            suffixLength += elementLength;
        }

        var remaining = contentBudget - prefixLength - suffixLength;
        while (prefixCount < suffixStartElement)
        {
            var elementLength = elementEnds[prefixCount] - elementStarts[prefixCount];
            if (elementLength > remaining)
                break;
            prefixLength += elementLength;
            prefixCount++;
            remaining -= elementLength;
        }
        while (suffixStartElement > prefixCount)
        {
            var candidate = suffixStartElement - 1;
            var elementLength = elementEnds[candidate] - elementStarts[candidate];
            if (elementLength > remaining)
                break;
            suffixStartElement = candidate;
            suffixLength += elementLength;
            remaining -= elementLength;
        }

        var suffixStart = suffixStartElement < elementStarts.Length
            ? elementStarts[suffixStartElement]
            : preferredStem.Length;
        return preferredStem[..prefixLength] +
               omissionMarker +
               preferredStem[suffixStart..] +
               extension;
    }

    [GeneratedRegex("[<>:\"/\\\\|?*]")]
    private static partial Regex IllegalCharsRegex();
}
