using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Widgets.App.Interop;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Widgets.App.Services;

/// <summary>
/// Renders the desktop wallpaper into cached images that widgets composite behind themselves.
/// Widget windows are non-activating, which makes Windows disable acrylic/Mica for them, so the
/// only way to look transparent is to draw the wallpaper ourselves — legitimate because widgets
/// sit at the bottom of the z-order and never have anything but the desktop behind them.
/// </summary>
public sealed class WallpaperService : IDisposable
{
    private const int StyleCenter = 0;
    private const int StyleTile = 1;
    private const int StyleStretch = 2;
    private const int StyleFit = 6;
    private const int StyleFill = 10;
    private const int StyleSpan = 22;

    /// <summary>Frosted output is stored small; it is blurred beyond the point where pixels matter.</summary>
    private const int FrostedDivisor = 4;

    /// <summary>Cadence for animated wallpapers; anything faster is not worth the re-encode.</summary>
    private static readonly TimeSpan AnimatedInterval = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _cacheDirectory = Path.Combine(Paths.DataDirectory, "cache");
    private readonly Timer _watch;
    private readonly Timer _animated;

    private string _signature = string.Empty;
    private RectInt32 _virtualScreen;
    private bool _captureAnimated;
    private int _rebuilding;
    private bool _disposed;

    public WallpaperService()
    {
        _virtualScreen = ReadVirtualScreen();
        _watch = new Timer(_ => Poll(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));
        _animated = new Timer(_ => OnAnimatedTick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Virtual-screen bounds, in physical pixels, that the composed image covers.</summary>
    public RectInt32 VirtualScreen => _virtualScreen;

    /// <summary>Re-capture the desktop on a timer so animated renderers stay in sync, at a CPU cost.</summary>
    public bool CaptureAnimated
    {
        get => _captureAnimated;
        set
        {
            if (_captureAnimated == value || _disposed)
            {
                return;
            }

            _captureAnimated = value;
            _animated.Change(
                value ? AnimatedInterval : Timeout.InfiniteTimeSpan,
                value ? AnimatedInterval : Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Raised after the wallpaper changed and the cache has been rebuilt.</summary>
    public event EventHandler? Changed;

    public string SharpPath => Path.Combine(_cacheDirectory, "wall.png");

    public string BlurredPath => Path.Combine(_cacheDirectory, "wall-blur.png");

    /// <summary>Absolute path to a cached PNG covering the whole virtual screen, or null.</summary>
    public async Task<string?> GetDesktopImageAsync(bool blurred, CancellationToken ct = default)
    {
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await BuildIfStaleAsync(false, ct).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }

            var path = blurred ? BlurredPath : SharpPath;
            return File.Exists(path) ? path : null;
        }
        catch (Exception ex)
        {
            Crash.Log(ex, nameof(GetDesktopImageAsync));
            return null;
        }
    }

    /// <summary>Rebuilds the cache unconditionally — the desktop can change without the wallpaper file doing so.</summary>
    public Task RefreshAsync(CancellationToken ct = default) => RebuildAsync(true, ct);

    public void Dispose()
    {
        _disposed = true;
        _animated.Dispose();
        _watch.Dispose();
        _gate.Dispose();
    }

    private void Poll()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (Signature(ReadVirtualScreen(), ResolveSource(), ReadStyle()) == _signature)
            {
                return;
            }

            _ = RebuildAsync(false, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Crash.Log(ex, "WallpaperService.Poll");
        }
    }

    private void OnAnimatedTick()
    {
        if (_disposed || !_captureAnimated)
        {
            return;
        }

        _ = RebuildAsync(true, CancellationToken.None);
    }

    private async Task RebuildAsync(bool force, CancellationToken ct)
    {
        // A slow capture must not let ticks pile up behind the gate.
        if (Interlocked.CompareExchange(ref _rebuilding, 1, 0) != 0)
        {
            return;
        }

        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            bool rebuilt;
            try
            {
                rebuilt = await BuildIfStaleAsync(force, ct).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }

            if (rebuilt && !_disposed)
            {
                AppServices.OnUi(() => Changed?.Invoke(this, EventArgs.Empty));
            }
        }
        catch (Exception ex)
        {
            Crash.Log(ex, "WallpaperService.Rebuild");
        }
        finally
        {
            Interlocked.Exchange(ref _rebuilding, 0);
        }
    }

    private async Task<bool> BuildIfStaleAsync(bool force, CancellationToken ct)
    {
        var screen = ReadVirtualScreen();
        var source = ResolveSource();
        var (style, tile) = ReadStyle();
        var signature = Signature(screen, source, (style, tile));

        if (!force && signature == _signature && File.Exists(SharpPath) && File.Exists(BlurredPath))
        {
            return false;
        }

        var built = await Task.Run(() => ComposeAsync(screen, source, style, tile, ct), ct).ConfigureAwait(false);
        if (!built)
        {
            return false;
        }

        _virtualScreen = screen;
        _signature = signature;
        return true;
    }

    // ---- Composition -----------------------------------------------------------

    private async Task<bool> ComposeAsync(RectInt32 screen, string? source, int style, bool tile, CancellationToken ct)
    {
        try
        {
            var width = screen.Width;
            var height = screen.Height;
            if (width <= 0 || height <= 0)
            {
                return false;
            }

            // A third-party renderer (Wallpaper Engine and friends) paints something the wallpaper
            // file knows nothing about, so what the desktop actually shows wins over composing it.
            var canvas = CaptureDesktop(width, height);

            if (canvas is null)
            {
                canvas = new byte[width * height * 4];
                FillSolid(canvas, ReadDesktopColor());

                if (source is not null)
                {
                    await PaintWallpaperAsync(canvas, width, height, screen, source, style, tile).ConfigureAwait(false);
                }
            }

            ct.ThrowIfCancellationRequested();

            Directory.CreateDirectory(_cacheDirectory);
            await WritePngAsync(canvas, width, height, SharpPath).ConfigureAwait(false);

            var frostedWidth = Math.Max(1, width / FrostedDivisor);
            var frostedHeight = Math.Max(1, height / FrostedDivisor);
            var frosted = Frost(canvas, width, height, frostedWidth, frostedHeight);
            await WritePngAsync(frosted, frostedWidth, frostedHeight, BlurredPath).ConfigureAwait(false);

            return true;
        }
        catch (Exception ex)
        {
            Crash.Log(ex, "WallpaperService.Compose");
            return false;
        }
    }

    // ---- Desktop capture -------------------------------------------------------

    /// <summary>
    /// Pixels of the surface the shell draws the wallpaper on, already laid out across every
    /// monitor, or null when it cannot be read (GPU renderers often print as a flat black frame).
    /// Only that surface is printed, so widgets sitting above it never appear in the result.
    /// </summary>
    private static byte[]? CaptureDesktop(int width, int height)
    {
        var target = FindDesktopSurface();
        if (target == IntPtr.Zero)
        {
            return null;
        }

        var dc = IntPtr.Zero;
        var dib = IntPtr.Zero;
        var previous = IntPtr.Zero;

        try
        {
            dc = CreateCompatibleDC(IntPtr.Zero);
            if (dc == IntPtr.Zero)
            {
                return null;
            }

            var info = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = BI_RGB,
                },
            };

            dib = CreateDIBSection(dc, ref info, DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero)
            {
                return null;
            }

            previous = SelectObject(dc, dib);
            if (!PrintWindow(target, dc, PW_RENDERFULLCONTENT))
            {
                return null;
            }

            var pixels = new byte[width * height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);

            if (IsUniform(pixels))
            {
                return null;
            }

            for (var i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 0xFF;
            }

            return pixels;
        }
        catch (Exception ex)
        {
            Crash.Log(ex, "WallpaperService.CaptureDesktop");
            return null;
        }
        finally
        {
            if (previous != IntPtr.Zero)
            {
                SelectObject(dc, previous);
            }

            if (dib != IntPtr.Zero)
            {
                Win32.DeleteObject(dib);
            }

            if (dc != IntPtr.Zero)
            {
                DeleteDC(dc);
            }
        }
    }

    private static IntPtr FindDesktopSurface()
    {
        try
        {
            var progman = Win32.FindWindow("Progman", null);
            if (progman == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            // Makes the shell split the desktop in two, which is how third-party renderers get
            // their own WorkerW to paint on; harmless when one already exists.
            Win32.SendMessageTimeout(
                progman, WM_SPAWN_WORKER, IntPtr.Zero, IntPtr.Zero, Win32.SMTO_NORMAL, 1000, out _);

            var wallpaper = IntPtr.Zero;

            EnumWindows((hwnd, _) =>
            {
                if (ClassOf(hwnd) != "WorkerW"
                    || Win32.FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero)
                {
                    return true;
                }

                // The icons live on this WorkerW; the wallpaper is on the one behind it.
                wallpaper = Win32.FindWindowEx(IntPtr.Zero, hwnd, "WorkerW", null);
                return wallpaper == IntPtr.Zero;
            }, IntPtr.Zero);

            return wallpaper != IntPtr.Zero ? wallpaper : progman;
        }
        catch (Exception ex)
        {
            Crash.Log(ex, "WallpaperService.FindDesktopSurface");
            return IntPtr.Zero;
        }
    }

    private static string ClassOf(IntPtr hwnd)
    {
        var buffer = new StringBuilder(64);
        var length = Win32.GetClassName(hwnd, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString(0, length) : string.Empty;
    }

    private static bool IsUniform(byte[] pixels)
    {
        for (var i = 4; i < pixels.Length; i += 4)
        {
            if (pixels[i] != pixels[0] || pixels[i + 1] != pixels[1] || pixels[i + 2] != pixels[2])
            {
                return false;
            }
        }

        return true;
    }

    // ---- Wallpaper file ---------------------------------------------------------

    private static async Task PaintWallpaperAsync(
        byte[] canvas, int width, int height, RectInt32 screen, string source, int style, bool tile)
    {
        using var stream = await OpenReadAsync(source).ConfigureAwait(false);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var imageWidth = (int)decoder.PixelWidth;
        var imageHeight = (int)decoder.PixelHeight;
        if (imageWidth <= 0 || imageHeight <= 0)
        {
            return;
        }

        if (tile || style == StyleTile)
        {
            var (pixels, w, h) = await DecodeAsync(decoder, imageWidth, imageHeight, null).ConfigureAwait(false);
            foreach (var monitor in ReadMonitors(screen))
            {
                for (var y = monitor.Y - screen.Y; y < monitor.Y - screen.Y + monitor.Height; y += h)
                {
                    for (var x = monitor.X - screen.X; x < monitor.X - screen.X + monitor.Width; x += w)
                    {
                        Blit(pixels, w, h, canvas, width, height, x, y);
                    }
                }
            }

            return;
        }

        var targets = style == StyleSpan
            ? new[] { new RectInt32(0, 0, width, height) }
            : ReadMonitors(screen)
                .Select(m => new RectInt32(m.X - screen.X, m.Y - screen.Y, m.Width, m.Height))
                .ToArray();

        // Span behaves like Fill across the whole virtual screen.
        var effective = style == StyleSpan ? StyleFill : style;

        foreach (var target in targets)
        {
            await PaintOneAsync(canvas, width, height, decoder, imageWidth, imageHeight, target, effective)
                .ConfigureAwait(false);
        }
    }

    private static async Task PaintOneAsync(
        byte[] canvas, int width, int height, BitmapDecoder decoder,
        int imageWidth, int imageHeight, RectInt32 target, int style)
    {
        int scaledWidth;
        int scaledHeight;

        switch (style)
        {
            case StyleStretch:
                scaledWidth = target.Width;
                scaledHeight = target.Height;
                break;

            case StyleFit:
            {
                var s = Math.Min((double)target.Width / imageWidth, (double)target.Height / imageHeight);
                scaledWidth = Math.Max(1, (int)Math.Round(imageWidth * s));
                scaledHeight = Math.Max(1, (int)Math.Round(imageHeight * s));
                break;
            }

            case StyleCenter:
                scaledWidth = imageWidth;
                scaledHeight = imageHeight;
                break;

            default:
            {
                var s = Math.Max((double)target.Width / imageWidth, (double)target.Height / imageHeight);
                scaledWidth = Math.Max(1, (int)Math.Round(imageWidth * s));
                scaledHeight = Math.Max(1, (int)Math.Round(imageHeight * s));
                break;
            }
        }

        BitmapBounds? crop = null;
        if (scaledWidth > target.Width || scaledHeight > target.Height)
        {
            var cropWidth = Math.Min(scaledWidth, target.Width);
            var cropHeight = Math.Min(scaledHeight, target.Height);
            crop = new BitmapBounds
            {
                X = (uint)Math.Max(0, (scaledWidth - cropWidth) / 2),
                Y = (uint)Math.Max(0, (scaledHeight - cropHeight) / 2),
                Width = (uint)cropWidth,
                Height = (uint)cropHeight,
            };
        }

        var (pixels, w, h) = await DecodeAsync(decoder, scaledWidth, scaledHeight, crop).ConfigureAwait(false);
        Blit(pixels, w, h, canvas, width, height,
            target.X + ((target.Width - w) / 2), target.Y + ((target.Height - h) / 2));
    }

    private static async Task<(byte[] Pixels, int Width, int Height)> DecodeAsync(
        BitmapDecoder decoder, int scaledWidth, int scaledHeight, BitmapBounds? crop)
    {
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, scaledWidth),
            ScaledHeight = (uint)Math.Max(1, scaledHeight),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };

        if (crop.HasValue)
        {
            transform.Bounds = crop.Value;
        }

        var data = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);

        var width = crop.HasValue ? (int)crop.Value.Width : scaledWidth;
        var height = crop.HasValue ? (int)crop.Value.Height : scaledHeight;
        return (data.DetachPixelData(), width, height);
    }

    private static void Blit(byte[] src, int srcWidth, int srcHeight, byte[] dst, int dstWidth, int dstHeight, int x, int y)
    {
        var x0 = Math.Max(0, -x);
        var x1 = Math.Min(srcWidth, dstWidth - x);
        if (x1 <= x0)
        {
            return;
        }

        var run = (x1 - x0) * 4;
        for (var row = Math.Max(0, -y); row < srcHeight; row++)
        {
            var destRow = y + row;
            if (destRow >= dstHeight)
            {
                break;
            }

            System.Buffer.BlockCopy(src, ((row * srcWidth) + x0) * 4, dst, ((destRow * dstWidth) + x + x0) * 4, run);
        }
    }

    private static void FillSolid(byte[] canvas, (byte R, byte G, byte B) color)
    {
        for (var i = 0; i < canvas.Length; i += 4)
        {
            canvas[i] = color.B;
            canvas[i + 1] = color.G;
            canvas[i + 2] = color.R;
            canvas[i + 3] = 0xFF;
        }
    }

    // ---- Frosting --------------------------------------------------------------

    private static byte[] Frost(byte[] source, int width, int height, int outWidth, int outHeight)
    {
        var smallWidth = Math.Max(1, width / 8);
        var smallHeight = Math.Max(1, height / 8);
        var small = Downscale(source, width, height, smallWidth, smallHeight);

        BoxBlur(small, smallWidth, smallHeight, 2);
        BoxBlur(small, smallWidth, smallHeight, 2);

        var result = Upscale(small, smallWidth, smallHeight, outWidth, outHeight);

        // A touch of mid-grey stops the blur from reading as a smudge of the wallpaper's colours.
        const double toward = 0.12;
        for (var i = 0; i < result.Length; i += 4)
        {
            result[i] = (byte)(result[i] + ((128 - result[i]) * toward));
            result[i + 1] = (byte)(result[i + 1] + ((128 - result[i + 1]) * toward));
            result[i + 2] = (byte)(result[i + 2] + ((128 - result[i + 2]) * toward));
            result[i + 3] = 0xFF;
        }

        return result;
    }

    private static byte[] Downscale(byte[] src, int srcWidth, int srcHeight, int dstWidth, int dstHeight)
    {
        var dst = new byte[dstWidth * dstHeight * 4];

        for (var y = 0; y < dstHeight; y++)
        {
            var y0 = y * srcHeight / dstHeight;
            var y1 = Math.Max(y0 + 1, (y + 1) * srcHeight / dstHeight);

            for (var x = 0; x < dstWidth; x++)
            {
                var x0 = x * srcWidth / dstWidth;
                var x1 = Math.Max(x0 + 1, (x + 1) * srcWidth / dstWidth);

                int b = 0, g = 0, r = 0, n = 0;
                for (var sy = y0; sy < y1; sy++)
                {
                    var row = sy * srcWidth * 4;
                    for (var sx = x0; sx < x1; sx++)
                    {
                        var i = row + (sx * 4);
                        b += src[i];
                        g += src[i + 1];
                        r += src[i + 2];
                        n++;
                    }
                }

                var o = ((y * dstWidth) + x) * 4;
                dst[o] = (byte)(b / n);
                dst[o + 1] = (byte)(g / n);
                dst[o + 2] = (byte)(r / n);
                dst[o + 3] = 0xFF;
            }
        }

        return dst;
    }

    private static void BoxBlur(byte[] pixels, int width, int height, int radius)
    {
        var temp = new byte[pixels.Length];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                int b = 0, g = 0, r = 0, n = 0;
                for (var k = -radius; k <= radius; k++)
                {
                    var sx = Math.Clamp(x + k, 0, width - 1);
                    var i = ((y * width) + sx) * 4;
                    b += pixels[i];
                    g += pixels[i + 1];
                    r += pixels[i + 2];
                    n++;
                }

                var o = ((y * width) + x) * 4;
                temp[o] = (byte)(b / n);
                temp[o + 1] = (byte)(g / n);
                temp[o + 2] = (byte)(r / n);
                temp[o + 3] = 0xFF;
            }
        }

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                int b = 0, g = 0, r = 0, n = 0;
                for (var k = -radius; k <= radius; k++)
                {
                    var sy = Math.Clamp(y + k, 0, height - 1);
                    var i = ((sy * width) + x) * 4;
                    b += temp[i];
                    g += temp[i + 1];
                    r += temp[i + 2];
                    n++;
                }

                var o = ((y * width) + x) * 4;
                pixels[o] = (byte)(b / n);
                pixels[o + 1] = (byte)(g / n);
                pixels[o + 2] = (byte)(r / n);
                pixels[o + 3] = 0xFF;
            }
        }
    }

    private static byte[] Upscale(byte[] src, int srcWidth, int srcHeight, int dstWidth, int dstHeight)
    {
        var dst = new byte[dstWidth * dstHeight * 4];
        var stepX = (double)srcWidth / dstWidth;
        var stepY = (double)srcHeight / dstHeight;

        for (var y = 0; y < dstHeight; y++)
        {
            var fy = Math.Clamp(((y + 0.5) * stepY) - 0.5, 0, srcHeight - 1);
            var y0 = (int)fy;
            var y1 = Math.Min(y0 + 1, srcHeight - 1);
            var wy = fy - y0;

            for (var x = 0; x < dstWidth; x++)
            {
                var fx = Math.Clamp(((x + 0.5) * stepX) - 0.5, 0, srcWidth - 1);
                var x0 = (int)fx;
                var x1 = Math.Min(x0 + 1, srcWidth - 1);
                var wx = fx - x0;

                var i00 = ((y0 * srcWidth) + x0) * 4;
                var i01 = ((y0 * srcWidth) + x1) * 4;
                var i10 = ((y1 * srcWidth) + x0) * 4;
                var i11 = ((y1 * srcWidth) + x1) * 4;
                var o = ((y * dstWidth) + x) * 4;

                for (var c = 0; c < 3; c++)
                {
                    var top = (src[i00 + c] * (1 - wx)) + (src[i01 + c] * wx);
                    var bottom = (src[i10 + c] * (1 - wx)) + (src[i11 + c] * wx);
                    dst[o + c] = (byte)Math.Clamp((top * (1 - wy)) + (bottom * wy), 0, 255);
                }

                dst[o + 3] = 0xFF;
            }
        }

        return dst;
    }

    // ---- I/O -------------------------------------------------------------------

    private static async Task<IRandomAccessStream> OpenReadAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        var stream = new InMemoryRandomAccessStream();
        var writer = new DataWriter(stream);
        writer.WriteBytes(bytes);
        await writer.StoreAsync();
        writer.DetachStream();
        writer.Dispose();
        stream.Seek(0);
        return stream;
    }

    private static async Task WritePngAsync(byte[] pixels, int width, int height, string path)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, pixels);
        await encoder.FlushAsync();

        stream.Seek(0);
        var bytes = new byte[stream.Size];
        var reader = new DataReader(stream);
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        reader.DetachStream();
        reader.Dispose();

        var temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, bytes).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    // ---- System state ----------------------------------------------------------

    private static string Signature(RectInt32 screen, string? source, (int Style, bool Tile) style)
    {
        var stamp = source is null ? "-" : File.GetLastWriteTimeUtc(source).Ticks.ToString();
        return $"{source}|{stamp}|{screen.X},{screen.Y},{screen.Width},{screen.Height}|{style.Style}|{style.Tile}";
    }

    private static string? ResolveSource()
    {
        try
        {
            var path = WindowHelper.GetWallpaperPath();
            if (path is not null)
            {
                return path;
            }

            var transcoded = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Themes", "TranscodedWallpaper");
            return File.Exists(transcoded) ? transcoded : null;
        }
        catch (Exception ex)
        {
            Crash.Log(ex, "WallpaperService.ResolveSource");
            return null;
        }
    }

    private static (int Style, bool Tile) ReadStyle()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", writable: false);
            var style = int.TryParse(key?.GetValue("WallpaperStyle") as string, out var parsed) ? parsed : StyleFill;
            var tile = (key?.GetValue("TileWallpaper") as string) == "1";
            return (style, tile);
        }
        catch (Exception ex)
        {
            Crash.Log(ex, "WallpaperService.ReadStyle");
            return (StyleFill, false);
        }
    }

    private static (byte R, byte G, byte B) ReadDesktopColor()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Colors", writable: false);
            var parts = (key?.GetValue("Background") as string)?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts?.Length == 3
                && byte.TryParse(parts[0], out var r)
                && byte.TryParse(parts[1], out var g)
                && byte.TryParse(parts[2], out var b))
            {
                return (r, g, b);
            }
        }
        catch (Exception ex)
        {
            Crash.Log(ex, "WallpaperService.ReadDesktopColor");
        }

        return (0, 0, 0);
    }

    private static RectInt32 ReadVirtualScreen()
    {
        var width = Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN);
        var height = Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN);
        if (width <= 0 || height <= 0)
        {
            return new RectInt32(
                0, 0, Win32.GetSystemMetrics(Win32.SM_CXSCREEN), Win32.GetSystemMetrics(Win32.SM_CYSCREEN));
        }

        return new RectInt32(
            Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN),
            Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN),
            width,
            height);
    }

    private static List<RectInt32> ReadMonitors(RectInt32 screen)
    {
        var monitors = new List<RectInt32>();

        Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr handle, IntPtr hdc, ref Win32.RECT bounds, IntPtr data) =>
        {
            var info = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
            if (Win32.GetMonitorInfo(handle, ref info))
            {
                var r = info.rcMonitor;
                monitors.Add(new RectInt32(r.Left, r.Top, r.Width, r.Height));
            }

            return true;
        }, IntPtr.Zero);

        if (monitors.Count == 0)
        {
            monitors.Add(screen);
        }

        return monitors;
    }

    // ---- Capture interop -------------------------------------------------------

    private const uint WM_SPAWN_WORKER = 0x052C;
    private const uint PW_RENDERFULLCONTENT = 0x00000002;
    private const uint BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(
        IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
}
