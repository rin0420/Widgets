using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Widgets.App.Controls;
using Widgets.App.Models;

namespace Widgets.App.Widgets;

/// <summary>A single photo, or a soft-fading slideshow over a folder.</summary>
public sealed partial class PhotoWidget : WidgetViewBase
{
    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".bmp", ".webp"];

    private readonly List<string> _files = [];

    private CancellationTokenSource? _cts;
    private int _index = -1;
    private bool _showingA;
    private int _intervalSeconds = 60;
    private int _decodeWidth = 512;

    public PhotoWidget()
    {
        InitializeComponent();
    }

    public override TimeSpan TickInterval
        => _files.Count > 1 ? TimeSpan.FromSeconds(Math.Clamp(_intervalSeconds, 3, 3600)) : TimeSpan.Zero;

    protected override void OnApply(WidgetRenderContext context)
    {
        CancelPending();

        var theme = context.Theme;
        var stretch = context.GetString(WidgetSettingKeys.PhotoStretch, "UniformToFill") == "Uniform"
            ? Stretch.Uniform
            : Stretch.UniformToFill;

        ImageA.Stretch = stretch;
        ImageB.Stretch = stretch;

        _intervalSeconds = context.GetInt(WidgetSettingKeys.PhotoIntervalSeconds, 60);
        _decodeWidth = (int)Math.Clamp(context.Width * 2, 128, 2048);

        WidgetVisuals.Style(EmptyText, theme, 12, WidgetVisuals.Secondary(theme));
        EmptyGlyph.FontFamily = WidgetVisuals.IconFont;
        EmptyGlyph.FontSize = WidgetVisuals.Size(theme, 28);
        EmptyGlyph.Foreground = new SolidColorBrush(WidgetVisuals.Secondary(theme));
        EmptyGlyph.Text = "";
        EmptyText.Text = "写真を選択してください";
        EmptyText.MaxWidth = context.Width;

        _files.Clear();
        _index = -1;

        var folder = context.GetString(WidgetSettingKeys.PhotoFolder);
        var single = context.GetString(WidgetSettingKeys.PhotoPath);

        if (!string.IsNullOrWhiteSpace(folder))
        {
            _files.AddRange(Enumerate(folder));
        }
        else if (!string.IsNullOrWhiteSpace(single) && File.Exists(single))
        {
            _files.Add(single);
        }

        if (_files.Count == 0)
        {
            EmptyState.Visibility = Visibility.Visible;
            ImageA.Source = null;
            ImageB.Source = null;
            ImageA.Opacity = 0;
            ImageB.Opacity = 0;
            return;
        }

        EmptyState.Visibility = Visibility.Collapsed;
        Advance();
    }

    public override void OnTick(DateTimeOffset now) => Advance();

    public override void Cleanup()
    {
        CancelPending();
        ImageA.Source = null;
        ImageB.Source = null;
    }

    private void CancelPending()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private void Advance()
    {
        if (_files.Count == 0)
        {
            return;
        }

        _index = (_index + 1) % _files.Count;

        CancelPending();
        _cts = new CancellationTokenSource();
        _ = ShowAsync(_files[_index], _cts.Token);
    }

    private async Task ShowAsync(string path, CancellationToken token)
    {
        try
        {
            var bitmap = new BitmapImage { DecodePixelWidth = _decodeWidth };

            // Open with sharing and dispose immediately so the widget never holds the user's file.
            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                await bitmap.SetSourceAsync(stream.AsRandomAccessStream()).AsTask(token).ConfigureAwait(true);
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            var incoming = _showingA ? ImageB : ImageA;
            var outgoing = _showingA ? ImageA : ImageB;
            _showingA = !_showingA;

            incoming.Source = bitmap;
            CrossFade(incoming, outgoing);
        }
        catch (OperationCanceledException)
        {
            // Superseded by the next slide.
        }
        catch (Exception ex)
        {
            Crash.Log(ex, $"PhotoWidget.ShowAsync({path})");
        }
    }

    private static void CrossFade(UIElement incoming, UIElement outgoing)
    {
        var duration = new Duration(TimeSpan.FromMilliseconds(450));
        var storyboard = new Storyboard();

        var fadeIn = new DoubleAnimation { To = 1, Duration = duration, EnableDependentAnimation = true };
        Storyboard.SetTarget(fadeIn, incoming);
        Storyboard.SetTargetProperty(fadeIn, "Opacity");
        storyboard.Children.Add(fadeIn);

        var fadeOut = new DoubleAnimation { To = 0, Duration = duration, EnableDependentAnimation = true };
        Storyboard.SetTarget(fadeOut, outgoing);
        Storyboard.SetTargetProperty(fadeOut, "Opacity");
        storyboard.Children.Add(fadeOut);

        storyboard.Begin();
    }

    private static IEnumerable<string> Enumerate(string folder)
    {
        try
        {
            if (!Directory.Exists(folder))
            {
                return [];
            }

            return Directory
                .EnumerateFiles(folder)
                .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            Crash.Log(ex, $"PhotoWidget.Enumerate({folder})");
            return [];
        }
    }
}
