using Avalonia;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using SkiaSharp;
using YAT.app.Clipboard;

namespace YAT.App.Tests;

public sealed class ClipboardImageResourceTests
{
    private static readonly Lock InitLock = new();
    private static bool _avaloniaInitialized;

    private static void EnsureAvalonia()
    {
        if (_avaloniaInitialized)
        {
            return;
        }

        lock (InitLock)
        {
            if (!_avaloniaInitialized)
            {
                if (Avalonia.Application.Current is null)
                {
                    AppBuilder.Configure<Avalonia.Application>()
                        .UseStandardRuntimePlatformSubsystem()
                        .UseWindowingSubsystem(static () => { }, "Test")
                        .UseSkia()
                        .UseHarfBuzz()
                        .SetupWithoutStarting();
                }

                _avaloniaInitialized = true;
            }
        }
    }

    private static byte[] CreateValidPngBytes()
    {
        using var bitmap = new SKBitmap(16, 16);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public async Task CopyPngAsync_Success_DisposesBitmapAfterFlushAndKeepsBitmapAliveDuringFlush()
    {
        EnsureAvalonia();
        var png = CreateValidPngBytes();
        Bitmap? captured = null;
        var clipboard = new FakeClipboardTarget();

        await AvaloniaGraphImageClipboard.CopyPngAsync(
            clipboard.SetDataAsync,
            clipboard.FlushAsync,
            stream => captured = new Bitmap(stream),
            png);

        Assert.NotNull(captured);
        Assert.Equal(1, clipboard.SetDataCalls);
        Assert.Equal(1, clipboard.FlushCalls);
        Assert.True(clipboard.BitmapReadableDuringFlush);
        Assert.Throws<ObjectDisposedException>(() => _ = captured!.PixelSize);
    }

    [Fact]
    public async Task CopyPngAsync_WhenSetDataThrows_DisposesBitmapAndPropagatesOriginalException()
    {
        EnsureAvalonia();
        var png = CreateValidPngBytes();
        Bitmap? captured = null;
        var expected = new InvalidOperationException("clipboard busy");
        var clipboard = new FakeClipboardTarget { ThrowOnSetData = expected };

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AvaloniaGraphImageClipboard.CopyPngAsync(
                clipboard.SetDataAsync,
                clipboard.FlushAsync,
                stream => captured = new Bitmap(stream),
                png));

        Assert.Same(expected, actual);
        Assert.NotNull(captured);
        Assert.Equal(0, clipboard.FlushCalls);
        Assert.Throws<ObjectDisposedException>(() => _ = captured!.PixelSize);
    }

    [Fact]
    public async Task CopyPngAsync_WhenFlushThrows_KeepsBitmapAliveForDelayedConsumerAndPropagatesOriginalException()
    {
        EnsureAvalonia();
        var png = CreateValidPngBytes();
        Bitmap? captured = null;
        var expected = new InvalidOperationException("flush failed");
        var clipboard = new FakeClipboardTarget { ThrowOnFlush = expected };

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AvaloniaGraphImageClipboard.CopyPngAsync(
                clipboard.SetDataAsync,
                clipboard.FlushAsync,
                stream => captured = new Bitmap(stream),
                png));

        Assert.Same(expected, actual);
        Assert.NotNull(captured);
        Assert.Equal(1, clipboard.SetDataCalls);
        Assert.Equal(1, clipboard.FlushCalls);
        Assert.Equal(new PixelSize(16, 16), captured!.PixelSize);

        // Simulate a downstream clipboard consumer performing delayed rendering after FlushAsync failed.
        var delayedBitmap = clipboard.ReadCurrentBitmapAsDelayedConsumer();
        Assert.NotNull(delayedBitmap);
        Assert.Same(captured, delayedBitmap);
        Assert.Equal(new PixelSize(16, 16), delayedBitmap!.PixelSize);

        captured.Dispose();
    }

    [Fact]
    public async Task CopyPngAsync_Repeated100Times_DisposesAllBitmaps()
    {
        EnsureAvalonia();
        var png = CreateValidPngBytes();
        var clipboard = new FakeClipboardTarget();
        var bitmaps = new List<Bitmap>(100);

        for (var i = 0; i < 100; i++)
        {
            await AvaloniaGraphImageClipboard.CopyPngAsync(
                clipboard.SetDataAsync,
                clipboard.FlushAsync,
                stream =>
                {
                    var bmp = new Bitmap(stream);
                    bitmaps.Add(bmp);
                    return bmp;
                },
                png);
        }

        Assert.Equal(100, bitmaps.Count);
        Assert.Equal(100, clipboard.FlushCalls);
        foreach (var bmp in bitmaps)
        {
            Assert.Throws<ObjectDisposedException>(() => _ = bmp.PixelSize);
        }
    }

    private sealed class FakeClipboardTarget
    {
        private IAsyncDataTransfer? _current;

        public Exception? ThrowOnSetData { get; init; }
        public Exception? ThrowOnFlush { get; init; }
        public int SetDataCalls { get; private set; }
        public int FlushCalls { get; private set; }
        public bool BitmapReadableDuringFlush { get; private set; }

        public Task SetDataAsync(IAsyncDataTransfer dataTransfer)
        {
            SetDataCalls++;
            if (ThrowOnSetData is not null)
            {
                throw ThrowOnSetData;
            }

            _current = dataTransfer;
            return Task.CompletedTask;
        }

        public Task FlushAsync()
        {
            FlushCalls++;
            if (ThrowOnFlush is not null)
            {
                throw ThrowOnFlush;
            }

            if (_current is IDataTransfer sync && sync.Items.Count > 0)
            {
                var bmp = sync.Items[0].TryGetRaw(DataFormat.Bitmap) as Bitmap;
                BitmapReadableDuringFlush = bmp is not null && bmp.PixelSize.Width == 16;
            }

            return Task.CompletedTask;
        }

        public Bitmap? ReadCurrentBitmapAsDelayedConsumer()
        {
            if (_current is IDataTransfer sync && sync.Items.Count > 0)
            {
                return sync.Items[0].TryGetRaw(DataFormat.Bitmap) as Bitmap;
            }

            return null;
        }
    }
}
