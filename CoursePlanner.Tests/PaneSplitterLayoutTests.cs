namespace CoursePlanner.Tests;

public sealed class PaneSplitterLayoutTests
{
    [Fact]
    public void DockedPaneSplittersUseInvisibleWideHitTargetsOverOnePixelEdgeDividers()
    {
        var xaml = Read("CoursePlanner", "Pages", "PlannerPage.xaml");
        var code = Read("CoursePlanner", "Pages", "PlannerPage.xaml.cs");
        var style = Segment(xaml, "<Style x:Key=\"SplitViewRailStyle\"", "</Style>");
        var columns = Segment(xaml, "<Grid.ColumnDefinitions>", "</Grid.ColumnDefinitions>");
        var librarySplitter = Element(xaml, "x:Name=\"LibrarySplitter\"");
        var libraryLine = Element(xaml, "x:Name=\"LibrarySplitterLine\"");
        var detailSplitter = Element(xaml, "x:Name=\"DetailSplitter\"");
        var detailLine = Element(xaml, "x:Name=\"DetailSplitterLine\"");

        Assert.Contains("Property=\"Width\" Value=\"10\"", style);
        Assert.Contains("Property=\"HorizontalAlignment\" Value=\"Left\"", style);
        Assert.Contains("Property=\"IsThumbVisible\" Value=\"False\"", style);
        Assert.Contains("Property=\"ManipulationMode\" Value=\"TranslateX,TranslateY\"", style);
        Assert.Contains("Property=\"ResizeBehavior\" Value=\"PreviousAndNext\"", style);
        Assert.Contains("x:Key=\"SizerBaseBackgroundPointerOver\" Color=\"Transparent\"", xaml);
        Assert.Contains("x:Key=\"SizerBaseBackgroundPressed\" Color=\"Transparent\"", xaml);
        Assert.DoesNotContain("Property=\"Opacity\"", style);
        Assert.DoesNotContain("<ControlTemplate", style);
        Assert.Contains("Grid.ColumnSpan=\"2\"", librarySplitter);
        Assert.Contains("Canvas.ZIndex=\"10\"", librarySplitter);
        Assert.Contains("Grid.ColumnSpan=\"2\"", detailSplitter);
        Assert.Contains("Canvas.ZIndex=\"10\"", detailSplitter);
        Assert.Equal(2, columns.Split("<ColumnDefinition Width=\"1\" />", StringSplitOptions.None).Length - 1);
        Assert.Contains("Width=\"1\"", libraryLine);
        Assert.Contains("Width=\"1\"", detailLine);
        Assert.DoesNotContain("Width=\"2\"", libraryLine);
        Assert.DoesNotContain("Width=\"2\"", detailLine);
        Assert.Contains("private const double PaneDividerWidth = 1;", code);
        Assert.Contains("splitterColumn.Width = new GridLength(PaneDividerWidth);", code);
        Assert.DoesNotContain("splitterColumn.Width = GridLength.Auto;", code);
    }

    private static string Element(string source, string marker)
    {
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing element marker: {marker}");
        var end = source.IndexOf("/>", start, StringComparison.Ordinal);
        Assert.True(end > start, $"Element is not self-closing: {marker}");
        return source[start..(end + 2)];
    }

    private static string Segment(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing start marker: {startMarker}");
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing end marker: {endMarker}");
        return source[start..(end + endMarker.Length)];
    }

    private static string Read(params string[] segments) =>
        File.ReadAllText(RepositoryPaths.FromRoot(segments));
}
