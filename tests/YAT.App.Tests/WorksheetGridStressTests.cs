using System.Collections.Concurrent;
using Avalonia;
using SkiaSharp;
using YAT.app.Composition;
using YAT.app.Views.WorksheetGrid;

namespace YAT.App.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WorksheetGridStressCollection
{
    public const string Name = "WorksheetGridStressCollection";
}

[Collection(WorksheetGridStressCollection.Name)]
public sealed class WorksheetGridStressTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void CreateSnapshotReusesCachedInstanceUntilResized()
    {
        var geometry = new WorksheetViewportGeometry([100, 120, 140]);
        var snap1 = geometry.CreateSnapshot();
        var snap2 = geometry.CreateSnapshot();
        Assert.Same(snap1, snap2);

        geometry.ResizeColumn(1, 200);
        var snap3 = geometry.CreateSnapshot();
        Assert.NotSame(snap1, snap3);
        Assert.Equal(120, snap1.GetColumnWidth(1));
        Assert.Equal(200, snap3.GetColumnWidth(1));
        Assert.Equal(360, snap1.TotalDataWidth);
        Assert.Equal(440, snap3.TotalDataWidth);
    }

    [Fact]
    public async Task ConcurrentResizeAndSnapshotRenderingNeverObservesTornGeometry()
    {
        var geometry = new WorksheetViewportGeometry(Enumerable.Repeat(100.0, 500).ToArray());
        using var renderer = new SkiaWorksheetRenderer();

        var snapshots = new ConcurrentQueue<WorksheetViewportGeometrySnapshot>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var resizeDone = false;

        var uiTask = Task.Run(() =>
        {
            var random = new Random(123);
            for (var i = 0; i < 300; i++)
            {
                geometry.ResizeColumn(i % 500, 60 + random.Next(0, 200));
                if (i % 3 == 0)
                {
                    snapshots.Enqueue(geometry.CreateSnapshot());
                }
            }

            Volatile.Write(ref resizeDone, true);
        }, cts.Token);

        var renderTask = Task.Run(() =>
        {
            using var bitmap = new SKBitmap(800, 400);
            using var canvas = new SKCanvas(bitmap);
            var selected = new HashSet<int> { 2 };
            var rendered = 0;

            while (!Volatile.Read(ref resizeDone) || !snapshots.IsEmpty)
            {
                if (!snapshots.TryDequeue(out var snap))
                {
                    Thread.Yield();
                    continue;
                }

                double expectedOffset = 0;
                for (var c = 0; c < snap.ColumnCount; c++)
                {
                    Assert.Equal(expectedOffset, snap.GetColumnLeftOffset(c), 6);
                    expectedOffset += snap.GetColumnWidth(c);
                    Assert.Equal(expectedOffset, snap.GetColumnRightOffset(c), 6);
                }

                Assert.Equal(expectedOffset, snap.TotalDataWidth, 6);

                renderer.Render(
                    canvas,
                    new SKSize(800, 400),
                    snap,
                    WorksheetViewportRenderSnapshot.Empty,
                    scrollX: 500,
                    scrollY: 100,
                    selected,
                    activeColumn: 2,
                    WorksheetTheme.Light);

                rendered++;
            }

            Assert.True(rendered >= 50);
        }, cts.Token);

        await Task.WhenAll(uiTask, renderTask);
    }

    [Fact]
    public async Task DrawOperationLifetimeStress_NeverDisposesWhileLeaseHeld()
    {
        var geometry = new WorksheetViewportGeometry(Enumerable.Repeat(100.0, 50).ToArray());
        var selected = new HashSet<int> { 0 };

        for (var cycle = 0; cycle < 100; cycle++)
        {
            var renderer = new SkiaWorksheetRenderer();
            var refCounted = new VirtualizedWorksheetCanvas.RefCountedRenderer(renderer);

            var ops = new VirtualizedWorksheetCanvas.DrawOperation[3];
            for (var i = 0; i < ops.Length; i++)
            {
                Assert.True(refCounted.TryAddRef());
                ops[i] = new VirtualizedWorksheetCanvas.DrawOperation(
                    new Rect(0, 0, 600, 300),
                    refCounted,
                    geometry.CreateSnapshot(),
                    WorksheetViewportRenderSnapshot.Empty,
                    scrollX: i * 50,
                    scrollY: i * 20,
                    selected,
                    activeColumn: 0,
                    WorksheetTheme.Light);
            }

            refCounted.Release();
            Assert.False(refCounted.IsDisposed);

            await Task.Run(() =>
            {
                using var bitmap = new SKBitmap(600, 300);
                using var canvas = new SKCanvas(bitmap);
                for (var i = 0; i < ops.Length; i++)
                {
                    Assert.False(refCounted.IsDisposed);
                    refCounted.Renderer.Render(
                        canvas,
                        new SKSize(600, 300),
                        geometry.CreateSnapshot(),
                        WorksheetViewportRenderSnapshot.Empty,
                        i * 50,
                        i * 20,
                        selected,
                        0,
                        WorksheetTheme.Light);
                    ops[i].Dispose();
                    if (i < ops.Length - 1)
                    {
                        Assert.False(refCounted.IsDisposed);
                    }
                }
            }, Token);

            Assert.True(refCounted.IsDisposed);
            Assert.False(refCounted.TryAddRef());
        }
    }

    [Fact]
    public void RepeatedFontFallbackCreationAndDisposalDoesNotLeakManagedMemory()
    {
        using var bitmap = new SKBitmap(800, 300);
        using var canvas = new SKCanvas(bitmap);
        using var primaryFont = new SKFont(SKTypeface.Default, 13f);
        using var paint = new SKPaint { IsAntialias = true };

        void RunCycle()
        {
            using var renderer = new SkiaWorksheetRenderer();
            using var text = new WorksheetTextRenderer();
            text.DrawClippedText(canvas, "晶圓測試_點位_001_良率_Bin1".AsSpan(), 10, 30, 300, SKTextAlign.Left, primaryFont, paint);
            renderer.Render(
                canvas,
                new SKSize(800, 300),
                new WorksheetViewportGeometry([100, 100]).CreateSnapshot(),
                WorksheetViewportRenderSnapshot.Empty,
                0,
                0,
                new HashSet<int>(),
                null,
                WorksheetTheme.Dark);
        }

        for (var i = 0; i < 5; i++)
        {
            RunCycle();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var managedBefore = GC.GetTotalMemory(true);

        for (var i = 0; i < 100; i++)
        {
            RunCycle();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var managedDeltaMb = (GC.GetTotalMemory(true) - managedBefore) / (1024.0 * 1024.0);
        Assert.True(managedDeltaMb < 5.0, $"Managed heap grew by {managedDeltaMb:F2} MB.");
    }
}
