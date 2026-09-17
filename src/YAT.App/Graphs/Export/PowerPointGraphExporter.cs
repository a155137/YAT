using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace YAT.app.Graphs.Export;

// The picture a presentation is built around: the PNG the graph was rendered to, its size in pixels (for the
// proportions alone) and the colour the slide behind it should be.
public sealed record PowerPointSlideImage
{
    public PowerPointSlideImage(byte[] png, int width, int height, string backgroundHex)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (png.Length == 0)
        {
            throw new ArgumentException("The image has no content.", nameof(png));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(backgroundHex);
        if (backgroundHex.Length != 6 || !backgroundHex.All(character => Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("The background must be six hexadecimal digits (RRGGBB).", nameof(backgroundHex));
        }

        Png = png;
        Width = width;
        Height = height;
        BackgroundHex = backgroundHex.ToUpperInvariant();
    }

    public byte[] Png { get; }

    public int Width { get; }

    public int Height { get; }

    public string BackgroundHex { get; }
}

// Writes a graph into a PowerPoint file.
public interface IPowerPointGraphExporter
{
    // A presentation of one slide holding this image. Overwrites the file at path.
    void Save(string path, PowerPointSlideImage image);
}

// Builds a .pptx with the Open XML SDK: a package of XML parts, so neither PowerPoint nor any other Office component
// has to be installed.
//
// The graph itself arrives as PNG bytes that were already rendered - the exporter never draws a graph - so the picture
// in the presentation is exactly the picture a PNG export would have written.
public sealed class PowerPointGraphExporter : IPowerPointGraphExporter
{
    public void Save(string path, PowerPointSlideImage image)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(image);

        using var document = PresentationDocument.Create(path, PresentationDocumentType.Presentation);

        var presentationPart = document.AddPresentationPart();
        presentationPart.Presentation = new P.Presentation();

        // A presentation is a graph of parts: the slide needs a layout, the layout a master, the master a theme, and
        // the presentation needs to know about the master and the slide.
        var slidePart = presentationPart.AddNewPart<SlidePart>("rId2");
        var layoutPart = slidePart.AddNewPart<SlideLayoutPart>("rId1");
        var masterPart = layoutPart.AddNewPart<SlideMasterPart>("rId1");
        var themePart = masterPart.AddNewPart<ThemePart>("rId2");

        masterPart.AddPart(layoutPart, "rId1");
        presentationPart.AddPart(masterPart, "rId1");
        presentationPart.AddPart(themePart, "rId3");

        var imagePart = slidePart.AddNewPart<ImagePart>("image/png", "rId3");
        using (var content = new MemoryStream(image.Png, writable: false))
        {
            imagePart.FeedData(content);
        }

        slidePart.Slide = Slide(slidePart.GetIdOfPart(imagePart), image);
        layoutPart.SlideLayout = Layout();
        masterPart.SlideMaster = Master(masterPart.GetIdOfPart(layoutPart));
        themePart.Theme = Theme();

        presentationPart.Presentation.Append(
            new P.SlideMasterIdList(new P.SlideMasterId { Id = 2147483648U, RelationshipId = "rId1" }),
            new P.SlideIdList(new P.SlideId { Id = 256U, RelationshipId = "rId2" }),
            new P.SlideSize { Cx = (int)SlideImageLayout.SlideWidth, Cy = (int)SlideImageLayout.SlideHeight },
            new P.NotesSize { Cx = 6858000, Cy = 9144000 });

        presentationPart.Presentation.Save();
    }

    // The one slide: the graph image, in its own proportions, in the middle, on a background that matches it.
    private static P.Slide Slide(string imageRelationshipId, PowerPointSlideImage image)
    {
        var placement = SlideImageLayout.Fit(
            SlideImageLayout.SlideWidth,
            SlideImageLayout.SlideHeight,
            SlideImageLayout.Margin,
            image.Width,
            image.Height);

        var picture = new P.Picture(
            new P.NonVisualPictureProperties(
                new P.NonVisualDrawingProperties { Id = 2U, Name = "Graph" },
                new P.NonVisualPictureDrawingProperties(new A.PictureLocks { NoChangeAspect = true }),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.BlipFill(
                new A.Blip { Embed = imageRelationshipId },
                new A.Stretch(new A.FillRectangle())),
            new P.ShapeProperties(
                new A.Transform2D(
                    new A.Offset { X = placement.X, Y = placement.Y },
                    new A.Extents { Cx = placement.Width, Cy = placement.Height }),
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }));

        return new P.Slide(
            new P.CommonSlideData(
                new P.Background(new P.BackgroundProperties(
                    new A.SolidFill(new A.RgbColorModelHex { Val = image.BackgroundHex }),
                    new A.EffectList())),
                ShapeTree(picture)),
            new P.ColorMapOverride(new A.MasterColorMapping()));
    }

    private static P.SlideLayout Layout() =>
        new(
            new P.CommonSlideData(ShapeTree()),
            new P.ColorMapOverride(new A.MasterColorMapping()))
        {
            Type = P.SlideLayoutValues.Blank
        };

    private static P.SlideMaster Master(string layoutRelationshipId) =>
        new(
            new P.CommonSlideData(ShapeTree()),
            new P.ColorMap
            {
                Background1 = A.ColorSchemeIndexValues.Light1,
                Text1 = A.ColorSchemeIndexValues.Dark1,
                Background2 = A.ColorSchemeIndexValues.Light2,
                Text2 = A.ColorSchemeIndexValues.Dark2,
                Accent1 = A.ColorSchemeIndexValues.Accent1,
                Accent2 = A.ColorSchemeIndexValues.Accent2,
                Accent3 = A.ColorSchemeIndexValues.Accent3,
                Accent4 = A.ColorSchemeIndexValues.Accent4,
                Accent5 = A.ColorSchemeIndexValues.Accent5,
                Accent6 = A.ColorSchemeIndexValues.Accent6,
                Hyperlink = A.ColorSchemeIndexValues.Hyperlink,
                FollowedHyperlink = A.ColorSchemeIndexValues.FollowedHyperlink
            },
            new P.SlideLayoutIdList(new P.SlideLayoutId { Id = 2147483649U, RelationshipId = layoutRelationshipId }));

    // Every shape tree starts with the properties of the group that holds it, whether or not it holds anything.
    private static P.ShapeTree ShapeTree(params OpenXmlElement[] shapes)
    {
        var tree = new P.ShapeTree(
            new P.NonVisualGroupShapeProperties(
                new P.NonVisualDrawingProperties { Id = 1U, Name = string.Empty },
                new P.NonVisualGroupShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.GroupShapeProperties(new A.TransformGroup()));

        foreach (var shape in shapes)
        {
            tree.Append(shape);
        }

        return tree;
    }

    // The smallest theme a master can point at and still be a valid presentation: the colours, fonts and the three
    // fill, line, effect and background styles the format requires.
    private static A.Theme Theme() =>
        new(
            new A.ThemeElements(
                new A.ColorScheme(
                    new A.Dark1Color(new A.SystemColor { Val = A.SystemColorValues.WindowText, LastColor = "000000" }),
                    new A.Light1Color(new A.SystemColor { Val = A.SystemColorValues.Window, LastColor = "FFFFFF" }),
                    new A.Dark2Color(new A.RgbColorModelHex { Val = "44546A" }),
                    new A.Light2Color(new A.RgbColorModelHex { Val = "E7E6E6" }),
                    new A.Accent1Color(new A.RgbColorModelHex { Val = "4472C4" }),
                    new A.Accent2Color(new A.RgbColorModelHex { Val = "ED7D31" }),
                    new A.Accent3Color(new A.RgbColorModelHex { Val = "A5A5A5" }),
                    new A.Accent4Color(new A.RgbColorModelHex { Val = "FFC000" }),
                    new A.Accent5Color(new A.RgbColorModelHex { Val = "5B9BD5" }),
                    new A.Accent6Color(new A.RgbColorModelHex { Val = "70AD47" }),
                    new A.Hyperlink(new A.RgbColorModelHex { Val = "0563C1" }),
                    new A.FollowedHyperlinkColor(new A.RgbColorModelHex { Val = "954F72" }))
                {
                    Name = "Office"
                },
                new A.FontScheme(
                    new A.MajorFont(
                        new A.LatinFont { Typeface = "Calibri Light" },
                        new A.EastAsianFont { Typeface = string.Empty },
                        new A.ComplexScriptFont { Typeface = string.Empty }),
                    new A.MinorFont(
                        new A.LatinFont { Typeface = "Calibri" },
                        new A.EastAsianFont { Typeface = string.Empty },
                        new A.ComplexScriptFont { Typeface = string.Empty }))
                {
                    Name = "Office"
                },
                new A.FormatScheme(
                    new A.FillStyleList(SchemeFill(), SchemeFill(), SchemeFill()),
                    new A.LineStyleList(SchemeLine(6350), SchemeLine(12700), SchemeLine(19050)),
                    new A.EffectStyleList(
                        new A.EffectStyle(new A.EffectList()),
                        new A.EffectStyle(new A.EffectList()),
                        new A.EffectStyle(new A.EffectList())),
                    new A.BackgroundFillStyleList(SchemeFill(), SchemeFill(), SchemeFill()))
                {
                    Name = "Office"
                }),
            new A.ObjectDefaults(),
            new A.ExtraColorSchemeList())
        {
            Name = "Office Theme"
        };

    private static A.SolidFill SchemeFill() =>
        new(new A.SchemeColor { Val = A.SchemeColorValues.PhColor });

    private static A.Outline SchemeLine(int width) =>
        new(
            new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }),
            new A.PresetDash { Val = A.PresetLineDashValues.Solid })
        {
            Width = width,
            CapType = A.LineCapValues.Flat,
            CompoundLineType = A.CompoundLineValues.Single,
            Alignment = A.PenAlignmentValues.Center
        };

    // Kept for the tests and for anyone reading a package by hand.
    internal static string Hex(byte red, byte green, byte blue) =>
        string.Create(CultureInfo.InvariantCulture, $"{red:X2}{green:X2}{blue:X2}");
}
