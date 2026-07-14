using System.Text;

namespace CoursePlanner.Core;

public enum CourseTypeSemantic
{
    Unknown,
    General,
    Major,
    Free
}

public enum StudyTypeSemantic
{
    Unknown,
    Core,
    Required,
    Elective
}

public static class CourseLabelSemantics
{
    public static CourseTypeSemantic ClassifyCourseType(string? value)
    {
        var label = NormalizedLabel.Create(value);
        if (label.IsEmpty)
            return CourseTypeSemantic.Unknown;

        var isMajor =
            !label.ContainsAny("跨专业") &&
            !label.Negates("专业", "major", "professional", "specialized", "discipline") &&
            (label.ContainsAny("专业", "学科基础") ||
             label.HasAnyToken("major", "professional", "specialized", "discipline"));
        var isGeneral =
            !label.Negates("通识", "general", "liberal", "public") &&
            (label.ContainsAny("通识", "公共基础", "公共教育") ||
             label.HasAnyToken("general", "liberal", "public") ||
             label.Compact is "gened" or "liberalarts");
        if (isMajor && isGeneral)
            return CourseTypeSemantic.Unknown;
        if (isMajor)
            return CourseTypeSemantic.Major;
        if (isGeneral)
            return CourseTypeSemantic.General;

        var isFree =
            !label.Negates("自由", "free", "unrestricted") &&
            !label.Negates("任选", "free", "unrestricted") &&
            !label.Negates("跨专业", "free", "unrestricted") &&
            (label.ContainsAny("自由", "任选", "任意选修", "跨专业") ||
             label.HasAnyToken("free", "unrestricted") ||
             label.Compact is "openelective" or "freelychosen");
        if (isFree)
            return CourseTypeSemantic.Free;

        return CourseTypeSemantic.Unknown;
    }

    public static StudyTypeSemantic ClassifyStudyType(string? value)
    {
        var label = NormalizedLabel.Create(value);
        if (label.IsEmpty)
            return StudyTypeSemantic.Unknown;

        if (!label.Negates("核心", "core") &&
            (label.ContainsAny("核心") || label.HasAnyToken("core")))
        {
            return StudyTypeSemantic.Core;
        }

        if (!label.Negates("必修", "required", "mandatory", "compulsory", "obligatory") &&
            !label.Negates("必选", "required", "mandatory", "compulsory", "obligatory") &&
            (label.ContainsAny("必修", "必选") ||
             label.HasAnyToken("required", "mandatory", "compulsory", "obligatory")))
        {
            return StudyTypeSemantic.Required;
        }

        if (!label.Negates("选修", "elective", "optional") &&
            !label.Negates("任选", "elective", "optional") &&
            !label.Negates("自选", "elective", "optional") &&
            (label.ContainsAny("选修", "任选", "自选") ||
             label.HasAnyToken("elective", "optional")))
        {
            return StudyTypeSemantic.Elective;
        }

        return StudyTypeSemantic.Unknown;
    }

    public static double Weight(CourseTypeSemantic type) => type switch
    {
        CourseTypeSemantic.Major => 1.2d,
        CourseTypeSemantic.General => 1.1d,
        CourseTypeSemantic.Free => 0.9d,
        _ => 1d
    };

    public static double Weight(StudyTypeSemantic type) => type switch
    {
        StudyTypeSemantic.Core => 2.75d,
        StudyTypeSemantic.Required => 2d,
        StudyTypeSemantic.Elective => 1d,
        _ => 1d
    };

    private sealed record NormalizedLabel(string Compact, IReadOnlyList<string> Tokens)
    {
        public bool IsEmpty => Compact.Length == 0;

        public bool ContainsAny(params string[] values) =>
            values.Any(value => Compact.Contains(value, StringComparison.Ordinal));

        public bool HasAnyToken(params string[] values) =>
            values.Any(value => Tokens.Contains(value, StringComparer.Ordinal));

        public bool Negates(string chineseValue, params string[] englishValues)
        {
            if (ContainsAny(
                    $"非{chineseValue}",
                    $"不是{chineseValue}",
                    $"不属于{chineseValue}",
                    $"不含{chineseValue}"))
            {
                return true;
            }

            foreach (var englishValue in englishValues)
            {
                if (Tokens.Contains($"non{englishValue}", StringComparer.Ordinal) ||
                    Tokens.Contains($"not{englishValue}", StringComparer.Ordinal) ||
                    Tokens.Contains($"without{englishValue}", StringComparer.Ordinal))
                {
                    return true;
                }

                for (var index = 1; index < Tokens.Count; index++)
                {
                    if (!string.Equals(Tokens[index], englishValue, StringComparison.Ordinal))
                        continue;
                    if (Tokens[index - 1] is "non" or "not" or "without")
                        return true;
                }
            }

            return false;
        }

        public static NormalizedLabel Create(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return new NormalizedLabel("", Array.Empty<string>());

            var source = TextRules.SanitizeUtf16(value).Trim().Normalize(NormalizationForm.FormKC).ToLowerInvariant();
            var builder = new StringBuilder(source.Length);
            foreach (var character in source)
            {
                if (char.IsLetterOrDigit(character))
                {
                    builder.Append(character);
                }
                else if (builder.Length > 0 && builder[^1] != ' ')
                {
                    builder.Append(' ');
                }
            }

            var tokens = builder
                .ToString()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return new NormalizedLabel(
                string.Concat(tokens),
                tokens);
        }
    }
}
