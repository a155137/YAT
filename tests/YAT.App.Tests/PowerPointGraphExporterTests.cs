using DocumentFormat.OpenXml.Packaging;
using YAT.App.Tests.TestDoubles;
using YAT.app.Graphs.Export;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace YAT.App.Tests;

// The .pptx an export writes, read back with the same Open XML SDK that wrote it. PowerPoint itself is never needed,
// here or on the machine YAT runs on.
public class PowerPointGraphExporterTests
{
    // Not a real PNG, but the exporter only carries the bytes; what matters is that they arrive unchanged.
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5];

    private static PowerPointSlideImage Image(string background = "FFFFFF") =>
        new(Png, GraphExportService.ExportWidth, GraphExportService.ExportHeight, background);

    private static string Save(TemporaryDirectory directory, PowerPointSlideImage? image = null, string name = "graph.pptx")
    {
        var path = directory.File(name);
        new PowerPointGraphExporter().Save(path, image ?? Image());
        return path;
    }

    // 1
    [Fact]
    public void APresentationIsWrittenAndCanBeOpenedAgain()
    {
        using var directory = new TemporaryDirectory();
        var path = Save(directory);

        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 0);

        using var document = PresentationDocument.Open(path, false);
        Assert.NotNull(document.PresentationPart);
        Assert.NotNull(document.PresentationPart!.Presentation);
    }

    // 2
    [Fact]
    public void ItHasExactlyOneSlideOfSixteenByNine()
    {
        using var directory = new TemporaryDirectory();
        using var document = PresentationDocument.Open(Save(directory), false);
        var presentationPart = document.PresentationPart!;

        Assert.Single(presentationPart.SlideParts);
        Assert.Single(presentationPart.Presentation!.SlideIdList!.Elements<P.SlideId>());

        var size = presentationPart.Presentation!.SlideSize!;
        Assert.Equal(SlideImageLayout.SlideWidth, size.Cx!.Value);
        Assert.Equal(SlideImageLayout.SlideHeight, size.Cy!.Value);
    }

    // 3
    [Fact]
    public void TheSlideCarriesTheExportedImage()
    {
        using var directory = new TemporaryDirectory();
        using var document = PresentationDocument.Open(Save(directory), false);
        var slidePart = document.PresentationPart!.SlideParts.Single();

        var imagePart = Assert.Single(slidePart.ImageParts);
        Assert.Equal("image/png", imagePart.ContentType);

        using var content = imagePart.GetStream();
        using var bytes = new MemoryStream();
        content.CopyTo(bytes);
        Assert.Equal(Png, bytes.ToArray());
    }

    // 4
    [Fact]
    public void ThePictureOnTheSlidePointsAtThatImage()
    {
        using var directory = new TemporaryDirectory();
        using var document = PresentationDocument.Open(Save(directory), false);
        var slidePart = document.PresentationPart!.SlideParts.Single();

        var picture = Assert.Single(slidePart.Slide!.Descendants<P.Picture>());
        var embed = picture.BlipFill!.Blip!.Embed!.Value!;

        Assert.Same(slidePart.ImageParts.Single(), slidePart.GetPartById(embed));
    }

    // 5
    [Fact]
    public void ThePictureIsPlacedWhereTheLayoutSaysAndKeepsItsProportions()
    {
        using var directory = new TemporaryDirectory();
        using var document = PresentationDocument.Open(Save(directory), false);
        var picture = document.PresentationPart!.SlideParts.Single().Slide!.Descendants<P.Picture>().Single();

        var expected = SlideImageLayout.Fit(
            SlideImageLayout.SlideWidth,
            SlideImageLayout.SlideHeight,
            SlideImageLayout.Margin,
            GraphExportService.ExportWidth,
            GraphExportService.ExportHeight);

        var transform = picture.ShapeProperties!.Transform2D!;
        Assert.Equal(expected.X, transform.Offset!.X!.Value);
        Assert.Equal(expected.Y, transform.Offset.Y!.Value);
        Assert.Equal(expected.Width, transform.Extents!.Cx!.Value);
        Assert.Equal(expected.Height, transform.Extents.Cy!.Value);
        Assert.True(picture.NonVisualPictureProperties!.NonVisualPictureDrawingProperties!.PictureLocks!.NoChangeAspect!.Value);
    }

    // 6
    [Fact]
    public void TheSlideBackgroundIsTheColourOfTheGraph()
    {
        using var directory = new TemporaryDirectory();
        using var document = PresentationDocument.Open(Save(directory, Image("202020")), false);
        var slide = document.PresentationPart!.SlideParts.Single().Slide!;

        var background = slide.CommonSlideData!.Background!;
        var fill = background.BackgroundProperties!.GetFirstChild<A.SolidFill>()!;

        Assert.Equal("202020", fill.RgbColorModelHex!.Val!.Value);
    }

    // 7
    [Fact]
    public void ItHasTheMasterLayoutAndThemeAPresentationNeeds()
    {
        using var directory = new TemporaryDirectory();
        using var document = PresentationDocument.Open(Save(directory), false);
        var presentationPart = document.PresentationPart!;

        var master = Assert.Single(presentationPart.SlideMasterParts);
        var layout = Assert.Single(master.SlideLayoutParts);

        Assert.NotNull(master.SlideMaster);
        Assert.NotNull(layout.SlideLayout);
        Assert.NotNull(master.ThemePart);
        Assert.NotNull(master.ThemePart!.Theme!.ThemeElements!.ColorScheme);
        Assert.NotNull(master.ThemePart!.Theme!.ThemeElements!.FontScheme);
        Assert.NotNull(master.ThemePart!.Theme!.ThemeElements!.FormatScheme);
        Assert.Single(presentationPart.Presentation!.SlideMasterIdList!.Elements<P.SlideMasterId>());
    }

    // 8
    [Fact]
    public void ExportingAgainReplacesTheFile()
    {
        using var directory = new TemporaryDirectory();
        var path = Save(directory);
        var first = new FileInfo(path).Length;

        Save(directory);

        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 0);
        Assert.Equal(first, new FileInfo(path).Length);
    }

    // 9
    [Fact]
    public void APresentationNeedsAPathAndAnImage()
    {
        using var directory = new TemporaryDirectory();
        var exporter = new PowerPointGraphExporter();

        Assert.Throws<ArgumentException>(() => exporter.Save(" ", Image()));
        Assert.Throws<ArgumentNullException>(() => exporter.Save(directory.File("x.pptx"), null!));
        Assert.Throws<ArgumentException>(() => new PowerPointSlideImage([], 100, 100, "FFFFFF"));
        Assert.Throws<ArgumentException>(() => new PowerPointSlideImage(Png, 100, 100, "white"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PowerPointSlideImage(Png, 0, 100, "FFFFFF"));
    }
}
