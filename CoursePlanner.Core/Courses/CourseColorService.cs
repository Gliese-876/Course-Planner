using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CoursePlanner.Core;

public static partial class CourseColorService
{
    public const string LightCourseBlockSurface = "#F8F8F8";
    public const string DarkCourseBlockSurface = "#303030";
    public const double MinimumGeneratedSurfaceContrast = 2.75;
    public const double MinimumGeneratedSurfaceVisualDistance = 0.16;

    private static readonly string[] SourcePalette =
    {
        "#C3637A", "#D25E3D", "#B97020", "#9D7D03", "#76892B", "#1B9449",
        "#00927F", "#008EA0", "#008DA7", "#706FFF", "#B455E7", "#B95FB8",
        "#C04B4D", "#AD5D24", "#976A00", "#777605", "#4A7F37", "#008261",
        "#007F83", "#007BA0", "#9649E3", "#9262FF", "#925DA7", "#A95683"
    };

    private static readonly string[] Palette = SourcePalette.Where(IsGeneratedColorSurfaceSafe).ToArray();

    public static IReadOnlyList<string> GeneratedPalette => Palette;

    private static readonly double MinimumGeneratedLuminance = Palette.Min(RelativeLuminance);
    private static readonly double MaximumGeneratedLuminance = Palette.Max(RelativeLuminance);

    public static bool IsValidHex(string? value) =>
        !string.IsNullOrWhiteSpace(value) && HexRegex().IsMatch(value);

    public static string Generate(int index) => Palette[Math.Abs(index % Palette.Length)];

    public static string GenerateForKey(string key) => Generate(StableHash(key));

    public static string EnsureValid(string? value, int index) =>
        IsValidHex(value) ? NormalizeHex(value!) : Generate(index);

    public static string NormalizeUserInput(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? ""
            : IsValidHex(value.Trim())
                ? NormalizeHex(value.Trim())
                : value.Trim();

    public static string NormalizeHex(string value) => value.Trim().ToUpperInvariant();

    public static bool ViolatesGeneratedBrightnessRange(string? value)
    {
        if (!IsValidHex(value))
            return false;

        var luminance = RelativeLuminance(value!);
        return luminance < MinimumGeneratedLuminance || luminance > MaximumGeneratedLuminance;
    }

    public static bool ViolatesGeneratedColorGuidance(string? value) =>
        IsValidHex(value) &&
        (ViolatesGeneratedBrightnessRange(value) || !IsGeneratedColorSurfaceSafe(value!));

    public static double ContrastRatio(string a, string b)
    {
        var l1 = RelativeLuminance(a);
        var l2 = RelativeLuminance(b);
        var lighter = Math.Max(l1, l2);
        var darker = Math.Min(l1, l2);
        return (lighter + 0.05) / (darker + 0.05);
    }

    public static double RelativeLuminance(string hex)
    {
        var (r, g, b) = ParseRgb(hex);
        static double Channel(int value)
        {
            var v = value / 255d;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(r) + 0.7152 * Channel(g) + 0.0722 * Channel(b);
    }

    public static double ColorVisualDistance(string a, string b)
    {
        var first = HslFromRgb(ParseRgb(a));
        var second = HslFromRgb(ParseRgb(b));
        var hueDistance = HueDistance(first.H, second.H) / 180d;
        var luminanceDistance = Math.Abs(RelativeLuminance(a) - RelativeLuminance(b)) * 2.4;
        var saturationDistance = Math.Abs(first.S - second.S);
        return (hueDistance * 0.58) + (Math.Clamp(luminanceDistance, 0, 1) * 0.34) + (saturationDistance * 0.08);
    }

    public static (int R, int G, int B) ParseRgb(string hex)
    {
        var value = NormalizeHex(hex).TrimStart('#');
        return (
            Convert.ToInt32(value[..2], 16),
            Convert.ToInt32(value.Substring(2, 2), 16),
            Convert.ToInt32(value.Substring(4, 2), 16));
    }

    private static bool IsGeneratedColorSurfaceSafe(string value) =>
        SurfaceSafe(value, LightCourseBlockSurface) && SurfaceSafe(value, DarkCourseBlockSurface);

    private static bool SurfaceSafe(string value, string surface) =>
        ContrastRatio(value, surface) >= MinimumGeneratedSurfaceContrast &&
        ColorVisualDistance(value, surface) >= MinimumGeneratedSurfaceVisualDistance;

    private static (double H, double S, double L) HslFromRgb((int R, int G, int B) rgb)
    {
        var r = rgb.R / 255d;
        var g = rgb.G / 255d;
        var b = rgb.B / 255d;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        var lightness = (max + min) / 2d;
        if (delta == 0)
            return (0, 0, lightness);

        var saturation = delta / (1d - Math.Abs((2d * lightness) - 1d));
        var hue = max == r
            ? 60d * (((g - b) / delta) % 6d)
            : max == g
                ? 60d * (((b - r) / delta) + 2d)
                : 60d * (((r - g) / delta) + 4d);
        if (hue < 0)
            hue += 360d;
        return (hue, saturation, lightness);
    }

    private static double HueDistance(double a, double b)
    {
        var direct = Math.Abs(a - b);
        return Math.Min(direct, 360d - direct);
    }

    private static int StableHash(string input)
    {
        var hash = unchecked((int)0x811C9DC5);
        foreach (var code in input)
        {
            hash ^= code;
            hash = unchecked(hash * 0x01000193);
        }
        return hash & 0x7FFFFFFF;
    }

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexRegex();
}
