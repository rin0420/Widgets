using Microsoft.UI.Xaml;
using Widgets.App.Controls;
using Widgets.App.Models;

namespace Widgets.App.Widgets;

/// <summary>Free-form text that shrinks itself until the whole note fits.</summary>
public sealed partial class NoteWidget : WidgetViewBase
{
    public NoteWidget()
    {
        InitializeComponent();
    }

    protected override void OnApply(WidgetRenderContext context)
    {
        var theme = context.Theme;
        var text = context.GetString(WidgetSettingKeys.NoteText, "メモを入力");

        var alignment = context.GetString(WidgetSettingKeys.NoteAlignment, "Center") switch
        {
            "Left" => TextAlignment.Left,
            "Right" => TextAlignment.Right,
            _ => TextAlignment.Center,
        };

        // The base size assumes a short note; the Viewbox takes over once the text outgrows the widget.
        var baseSize = context.Size switch
        {
            WidgetSize.Small => 17.0,
            WidgetSize.Medium => 20.0,
            WidgetSize.Large => 24.0,
            WidgetSize.Wide => 22.0,
            WidgetSize.Tall => 19.0,
            _ => 17.0,
        };

        var density = Math.Max(1, text.Length / (context.Size == WidgetSize.Small ? 40.0 : 90.0));
        WidgetVisuals.Style(NoteText, theme, baseSize / Math.Sqrt(density), WidgetVisuals.Tint(theme));

        NoteText.Text = text;
        NoteText.TextAlignment = alignment;
        NoteText.Width = context.Width;
        NoteText.LineHeight = NoteText.FontSize * 1.35;

        Box.MaxWidth = context.Width;
        Box.MaxHeight = context.Height;
    }
}
