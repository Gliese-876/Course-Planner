using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Buffers.Binary;
using Windows.ApplicationModel;

namespace CoursePlanner.Services;

public static class AppTypography
{
    private const string AppFontResourceKey = "AppTextFontFamily";
    private const string AppSemiBoldFontResourceKey = "AppTextSemiBoldFontFamily";
    private const string AppBoldFontResourceKey = "AppTextBoldFontFamily";
    private const string CourseBlockFontResourceKey = "CourseBlockTextFontFamily";
    private const string CourseBlockBoldFontResourceKey = "CourseBlockTextBoldFontFamily";
    private const string DreamHanFamilyName = "Dream Han Sans SC";
    private const double DefaultBodyFontSize = 14;
    private const double DefaultBodyStrongFontSize = 14;
    private const double DefaultTitleFontSize = 28;
    private const double DefaultSubtitleFontSize = 20;
    private const double DefaultCaptionFontSize = 12;
    private static readonly Dictionary<string, OpenTypeFontMetrics> MetricsCache = new(StringComparer.Ordinal);

    public static FontFamily FontFamily => (FontFamily)Application.Current.Resources[AppFontResourceKey];

    public static FontFamily SemiBoldFontFamily => (FontFamily)Application.Current.Resources[AppSemiBoldFontResourceKey];

    public static FontFamily BoldFontFamily => (FontFamily)Application.Current.Resources[AppBoldFontResourceKey];

    public static FontFamily CourseBlockFontFamily => (FontFamily)Application.Current.Resources[CourseBlockFontResourceKey];

    public static FontFamily CourseBlockBoldFontFamily => (FontFamily)Application.Current.Resources[CourseBlockBoldFontResourceKey];

    public static string FontFamilyName
    {
        get
        {
            var source = ResourceFontSource(AppFontResourceKey);
            var hash = source.IndexOf('#', StringComparison.Ordinal);
            if (hash < 0 || hash == source.Length - 1)
                throw new InvalidOperationException($"Font resource {AppFontResourceKey} must include a family name.");
            return source[(hash + 1)..];
        }
    }

    public static string RegularFontFilePath => FontFilePathForResource(AppFontResourceKey);

    public static string SemiBoldFontFilePath => FontFilePathForResource(AppSemiBoldFontResourceKey);

    public static string BoldFontFilePath => FontFilePathForResource(AppBoldFontResourceKey);

    public static string CourseBlockRegularFontFilePath => FontFilePathForResource(CourseBlockFontResourceKey);

    public static string CourseBlockBoldFontFilePath => FontFilePathForResource(CourseBlockBoldFontResourceKey);

    public static void InitializeResources()
    {
        SetLineHeightResource("AppBodyTextLineHeight", AppTextRole.Body);
        SetLineHeightResource("AppBodyStrongTextLineHeight", AppTextRole.BodyStrong);
        SetLineHeightResource("AppTitleTextLineHeight", AppTextRole.Title);
        SetLineHeightResource("AppSubtitleTextLineHeight", AppTextRole.Subtitle);
        SetLineHeightResource("AppCaptionTextLineHeight", AppTextRole.Caption);
        SetLineHeightResource("AppPickerDisplayTextLineHeight", AppTextRole.PickerDisplay);
        SetLineHeightResource("AppClockDigitTextLineHeight", AppTextRole.ClockDigit);
        SetLineHeightResource("AppToolbarTextLineHeight", AppTextRole.Body, 13);
        Application.Current.Resources["AppToolbarIconAlignmentOffset"] =
            MetricsForRole(AppTextRole.Body).IconAlignmentOffset(
                13,
                LineHeight(AppTextRole.Body, 13));
    }

    public static void Apply(Control root) => Apply(root, AppTextRole.Body);

    public static void Apply(Control root, AppTextRole role)
    {
        root.FontFamily = FontFamilyFor(role);
        root.FontSize = FontSize(role);
        root.FontWeight = FontWeightFor(role);
    }

    public static Style TextStyle(AppTextRole role) =>
        (Style)Application.Current.Resources[StyleResourceKey(role)];

    public static TextBlock TextBlock(
        string text,
        AppTextRole role = AppTextRole.Body,
        TextWrapping wrapping = TextWrapping.NoWrap)
    {
        return new TextBlock
        {
            Text = text,
            Style = TextStyle(role),
            TextWrapping = wrapping
        };
    }

    public static void ApplyTextMetrics(TextBlock text, AppTextRole role)
    {
        text.FontFamily = FontFamilyFor(role);
        text.FontSize = FontSize(role);
        text.FontWeight = FontWeightFor(role);
        text.LineHeight = LineHeight(role);
        text.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        text.TextLineBounds = TextLineBounds.Full;
    }

    public static FontFamily FontFamilyFor(AppTextRole role) =>
        role switch
        {
            AppTextRole.BodyStrong or AppTextRole.Title or AppTextRole.Subtitle => SemiBoldFontFamily,
            AppTextRole.ClockDigit => BoldFontFamily,
            _ => FontFamily
        };

    public static Windows.UI.Text.FontWeight FontWeightFor(AppTextRole role) =>
        role switch
        {
            AppTextRole.BodyStrong or AppTextRole.Title or AppTextRole.Subtitle => FontWeights.SemiBold,
            AppTextRole.ClockDigit => FontWeights.Bold,
            _ => FontWeights.Normal
        };

    public static double FontSize(AppTextRole role) =>
        ResourceDouble(FontSizeResourceKey(role), DefaultFontSize(role));

    public static double LineHeight(AppTextRole role) =>
        ResourceDouble(LineHeightResourceKey(role), MetricsForRole(role).LineHeight(FontSize(role)));

    public static double LineHeight(AppTextRole role, double fontSize) =>
        MetricsForRole(role).LineHeight(fontSize);

    public static double CourseBlockLineHeight(double fontSize, bool bold) =>
        MetricsForResource(bold ? CourseBlockBoldFontResourceKey : CourseBlockFontResourceKey)
            .CompactLineHeight(fontSize);

    public static double IconAlignmentOffset(AppTextRole role, double? lineHeight = null)
    {
        var fontSize = FontSize(role);
        return MetricsForRole(role).IconAlignmentOffset(fontSize, lineHeight ?? LineHeight(role));
    }

    private static string FontFilePathForResource(string resourceKey)
    {
        var source = ResourceFontSource(resourceKey);
        var hash = source.IndexOf('#', StringComparison.Ordinal);
        var uri = hash >= 0 ? source[..hash] : source;
        const string prefix = "ms-appx:///";
        if (!uri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Font resource {resourceKey} must use an ms-appx URI.");

        var relative = uri[prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(Package.Current.InstalledLocation.Path, relative);
    }

    private static string ResourceFontSource(string resourceKey) =>
        ((FontFamily)Application.Current.Resources[resourceKey]).Source;

    private static void SetLineHeightResource(string resourceKey, AppTextRole role)
    {
        Application.Current.Resources[resourceKey] = MetricsForRole(role).LineHeight(FontSize(role));
    }

    private static void SetLineHeightResource(string resourceKey, AppTextRole role, double fontSize)
    {
        Application.Current.Resources[resourceKey] = MetricsForRole(role).LineHeight(fontSize);
    }

    private static OpenTypeFontMetrics MetricsForRole(AppTextRole role) =>
        MetricsForResource(FontResourceKey(role));

    private static OpenTypeFontMetrics MetricsForResource(string resourceKey)
    {
        var source = ResourceFontSource(resourceKey);
        if (MetricsCache.TryGetValue(source, out var cached))
            return cached;

        var metrics = OpenTypeFontMetrics.Load(FontFilePathForResource(resourceKey), FontFamilyNameForResource(resourceKey));
        MetricsCache[source] = metrics;
        return metrics;
    }

    private static string FontResourceKey(AppTextRole role) =>
        role switch
        {
            AppTextRole.BodyStrong or AppTextRole.Title or AppTextRole.Subtitle => AppSemiBoldFontResourceKey,
            AppTextRole.ClockDigit => AppBoldFontResourceKey,
            _ => AppFontResourceKey
        };

    private static string StyleResourceKey(AppTextRole role) =>
        role switch
        {
            AppTextRole.BodyStrong => "BodyStrongTextBlockStyle",
            AppTextRole.Title => "TitleTextBlockStyle",
            AppTextRole.Subtitle => "SubtitleTextBlockStyle",
            AppTextRole.Caption => "CaptionTextBlockStyle",
            _ => "BodyTextBlockStyle"
        };

    private static string FontSizeResourceKey(AppTextRole role) =>
        role switch
        {
            AppTextRole.BodyStrong => "BodyStrongTextBlockFontSize",
            AppTextRole.Title => "TitleTextBlockFontSize",
            AppTextRole.Subtitle => "SubtitleTextBlockFontSize",
            AppTextRole.Caption => "CaptionTextBlockFontSize",
            AppTextRole.ClockDigit => "AppClockDigitTextFontSize",
            _ => "BodyTextBlockFontSize"
        };

    private static string LineHeightResourceKey(AppTextRole role) =>
        role switch
        {
            AppTextRole.BodyStrong => "AppBodyStrongTextLineHeight",
            AppTextRole.Title => "AppTitleTextLineHeight",
            AppTextRole.Subtitle => "AppSubtitleTextLineHeight",
            AppTextRole.Caption => "AppCaptionTextLineHeight",
            AppTextRole.PickerDisplay => "AppPickerDisplayTextLineHeight",
            AppTextRole.ClockDigit => "AppClockDigitTextLineHeight",
            _ => "AppBodyTextLineHeight"
        };

    private static double DefaultFontSize(AppTextRole role) =>
        role switch
        {
            AppTextRole.BodyStrong => DefaultBodyStrongFontSize,
            AppTextRole.Title => DefaultTitleFontSize,
            AppTextRole.Subtitle => DefaultSubtitleFontSize,
            AppTextRole.Caption => DefaultCaptionFontSize,
            AppTextRole.ClockDigit => 36,
            _ => DefaultBodyFontSize
        };

    private static string FontFamilyNameForResource(string resourceKey)
    {
        var source = ResourceFontSource(resourceKey);
        var hash = source.IndexOf('#', StringComparison.Ordinal);
        return hash >= 0 && hash < source.Length - 1 ? source[(hash + 1)..] : DreamHanFamilyName;
    }

    private static double ResourceDouble(string key, double fallback)
    {
        if (!Application.Current.Resources.TryGetValue(key, out var value))
            return fallback;

        return value switch
        {
            double number => number,
            float number => number,
            int number => number,
            _ => fallback
        };
    }

    private sealed record OpenTypeFontMetrics(
        int UnitsPerEm,
        int Ascender,
        int Descender,
        int LineGap,
        int CompactAscender,
        int CompactDescender,
        int CompactLineGap,
        int CapHeight)
    {
        private const uint TrueTypeCollectionTag = 0x74746366;
        private const uint HeadTag = 0x68656164;
        private const uint HorizontalHeaderTag = 0x68686561;
        private const uint NamingTag = 0x6E616D65;
        private const uint Os2Tag = 0x4F532F32;
        private const ushort UseTypographicMetricsFlag = 0x0080;

        public double LineHeight(double fontSize)
        {
            var natural = (Ascender - Descender + LineGap) * fontSize / UnitsPerEm;
            return Math.Ceiling(natural);
        }

        public double CompactLineHeight(double fontSize)
        {
            var compactNatural = (CompactAscender - CompactDescender + CompactLineGap) * fontSize / UnitsPerEm;
            return Math.Ceiling(Math.Max(fontSize, compactNatural) + 1);
        }

        public double IconAlignmentOffset(double fontSize, double lineHeight)
        {
            var scale = fontSize / UnitsPerEm;
            var contentHeight = (Ascender - Descender) * scale;
            var baseline = ((lineHeight - contentHeight) / 2) + (Ascender * scale);
            var capCenter = baseline - ((CapHeight * scale) / 2);
            return capCenter - (lineHeight / 2);
        }

        public static OpenTypeFontMetrics Load(string path, string familyName)
        {
            var data = File.ReadAllBytes(path);
            var fontOffset = SelectFontOffset(data, familyName);
            var tables = ReadTableDirectory(data, fontOffset);
            var headOffset = TableOffset(tables, HeadTag);
            var hheaOffset = TableOffset(tables, HorizontalHeaderTag);
            var unitsPerEm = ReadUInt16(data, headOffset + 18);
            var hheaAscender = ReadInt16(data, hheaOffset + 4);
            var hheaDescender = ReadInt16(data, hheaOffset + 6);
            var hheaLineGap = ReadInt16(data, hheaOffset + 8);

            if (!tables.TryGetValue(Os2Tag, out var os2))
            {
                return new OpenTypeFontMetrics(
                    unitsPerEm,
                    hheaAscender,
                    hheaDescender,
                    hheaLineGap,
                    hheaAscender,
                    hheaDescender,
                    hheaLineGap,
                    Math.Max(1, (int)hheaAscender));
            }

            var os2Offset = os2.Offset;
            var version = ReadUInt16(data, os2Offset);
            var fsSelection = ReadUInt16(data, os2Offset + 62);
            var useTypographicMetrics = (fsSelection & UseTypographicMetricsFlag) != 0;
            var typoAscender = ReadInt16(data, os2Offset + 68);
            var typoDescender = ReadInt16(data, os2Offset + 70);
            var typoLineGap = ReadInt16(data, os2Offset + 72);
            var capHeight = version >= 2 ? ReadInt16(data, os2Offset + 88) : 0;
            if (capHeight <= 0)
                capHeight = Math.Max(1, hheaAscender + hheaDescender);

            var compactAscender = typoAscender > 0 ? typoAscender : hheaAscender;
            var compactDescender = typoDescender < 0 ? typoDescender : hheaDescender;
            var compactLineGap = Math.Max(0, (int)typoLineGap);

            return useTypographicMetrics
                ? new OpenTypeFontMetrics(
                    unitsPerEm,
                    typoAscender,
                    typoDescender,
                    typoLineGap,
                    compactAscender,
                    compactDescender,
                    compactLineGap,
                    capHeight)
                : new OpenTypeFontMetrics(
                    unitsPerEm,
                    hheaAscender,
                    hheaDescender,
                    hheaLineGap,
                    compactAscender,
                    compactDescender,
                    compactLineGap,
                    capHeight);
        }

        private static int SelectFontOffset(byte[] data, string familyName)
        {
            var signature = ReadUInt32(data, 0);
            if (signature != TrueTypeCollectionTag)
                return 0;

            var count = ReadUInt32(data, 8);
            var fallback = (int)ReadUInt32(data, 12);
            for (var index = 0; index < count; index++)
            {
                var offset = (int)ReadUInt32(data, 12 + (index * 4));
                if (FontNames(data, offset).Any(name => string.Equals(name, familyName, StringComparison.OrdinalIgnoreCase)))
                    return offset;
            }

            return fallback;
        }

        private static IEnumerable<string> FontNames(byte[] data, int fontOffset)
        {
            var tables = ReadTableDirectory(data, fontOffset);
            if (!tables.TryGetValue(NamingTag, out var nameTable))
                yield break;

            var offset = nameTable.Offset;
            var count = ReadUInt16(data, offset + 2);
            var storageOffset = offset + ReadUInt16(data, offset + 4);
            for (var index = 0; index < count; index++)
            {
                var record = offset + 6 + (index * 12);
                var platformId = ReadUInt16(data, record);
                var nameId = ReadUInt16(data, record + 6);
                if (nameId is not (1 or 16))
                    continue;

                var length = ReadUInt16(data, record + 8);
                var stringOffset = storageOffset + ReadUInt16(data, record + 10);
                yield return DecodeName(data.AsSpan(stringOffset, length), platformId);
            }
        }

        private static string DecodeName(ReadOnlySpan<byte> bytes, ushort platformId)
        {
            if (platformId is 0 or 3)
            {
                Span<byte> copy = bytes.Length <= 256 ? stackalloc byte[bytes.Length] : new byte[bytes.Length];
                bytes.CopyTo(copy);
                for (var index = 0; index < copy.Length; index += 2)
                {
                    var first = copy[index];
                    copy[index] = copy[index + 1];
                    copy[index + 1] = first;
                }
                return System.Text.Encoding.Unicode.GetString(copy).TrimEnd('\0');
            }

            return System.Text.Encoding.Latin1.GetString(bytes).TrimEnd('\0');
        }

        private static Dictionary<uint, TableRecord> ReadTableDirectory(byte[] data, int fontOffset)
        {
            var tableCount = ReadUInt16(data, fontOffset + 4);
            var tables = new Dictionary<uint, TableRecord>();
            for (var index = 0; index < tableCount; index++)
            {
                var record = fontOffset + 12 + (index * 16);
                var tag = ReadUInt32(data, record);
                tables[tag] = new TableRecord((int)ReadUInt32(data, record + 8), (int)ReadUInt32(data, record + 12));
            }

            return tables;
        }

        private static int TableOffset(Dictionary<uint, TableRecord> tables, uint tag)
        {
            if (!tables.TryGetValue(tag, out var record))
                throw new InvalidOperationException($"Font table 0x{tag:X8} is required for typography metrics.");
            return record.Offset;
        }

        private static ushort ReadUInt16(byte[] data, int offset) =>
            BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));

        private static short ReadInt16(byte[] data, int offset) =>
            BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(offset, 2));

        private static uint ReadUInt32(byte[] data, int offset) =>
            BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));

        private readonly record struct TableRecord(int Offset, int Length);
    }
}

public enum AppTextRole
{
    Body,
    BodyStrong,
    Title,
    Subtitle,
    Caption,
    PickerDisplay,
    ClockDigit
}
