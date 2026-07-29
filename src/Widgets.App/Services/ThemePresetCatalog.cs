using Widgets.App.Models;

namespace Widgets.App.Services;

/// <summary>
/// The 16 built-in "Aesthetics" presets shown in the theme gallery. Every preset keeps
/// TintColor/BackgroundColor at or above the WCAG AA contrast ratio (4.5) so the default
/// look is always readable, regardless of which one a user picks.
/// </summary>
public static class ThemePresetCatalog
{
    public static IReadOnlyList<ThemePreset> BuiltIn { get; } =
    [
        // ---- ミニマル ------------------------------------------------------
        new()
        {
            Id = "minimal-white",
            Name = "ミニマル・ホワイト",
            Category = "ミニマル",
            Theme = new WidgetTheme
            {
                FontFamily = "Segoe UI Variable Display",
                FontScale = 1.0,
                FontWeight = 400,
                TintColor = "#FF1A1A1A",
                AccentColor = "#FF0067C0",
                SecondaryColor = "#FF6E6E6E",
                BackgroundColor = "#FFFFFFFF",
                BorderColor = "#FFE0E0E0",
                BorderThickness = 1.0,
                CornerRadius = 8.0,
                Backdrop = BackdropMode.Solid,
                Padding = 16.0,
                DropShadow = false,
            },
        },
        new()
        {
            Id = "minimal-black",
            Name = "ミニマル・ブラック",
            Category = "ミニマル",
            Theme = new WidgetTheme
            {
                FontFamily = "Segoe UI",
                FontScale = 1.0,
                FontWeight = 300,
                TintColor = "#FFFFFFFF",
                AccentColor = "#FF4CC2FF",
                SecondaryColor = "#FFAFAFAF",
                BackgroundColor = "#FF101010",
                BorderColor = "#FF2A2A2A",
                BorderThickness = 0.5,
                CornerRadius = 4.0,
                Backdrop = BackdropMode.Solid,
                Padding = 14.0,
                DropShadow = false,
            },
        },
        new()
        {
            Id = "minimal-clear",
            Name = "ミニマル・クリア",
            Category = "ミニマル",
            Theme = new WidgetTheme
            {
                FontFamily = "Segoe UI Variable Display",
                FontScale = 1.0,
                FontWeight = 500,
                TintColor = "#FF202020",
                AccentColor = "#FF005FB8",
                SecondaryColor = "#FF616161",
                BackgroundColor = "#00F5F5F5",
                BorderColor = "#00000000",
                BorderThickness = 0.0,
                CornerRadius = 12.0,
                Backdrop = BackdropMode.Clear,
                Padding = 10.0,
                DropShadow = false,
            },
        },

        // ---- ダーク --------------------------------------------------------
        new()
        {
            Id = "dark-obsidian",
            Name = "ダーク・オブシディアン",
            Category = "ダーク",
            Theme = new WidgetTheme
            {
                FontFamily = "Segoe UI",
                FontScale = 1.0,
                FontWeight = 400,
                TintColor = "#FFF5F5F5",
                AccentColor = "#FFFF6B6B",
                SecondaryColor = "#FFB0B0B0",
                BackgroundColor = "#F0121212",
                BorderColor = "#FF2A2A2A",
                BorderThickness = 1.0,
                CornerRadius = 20.0,
                Backdrop = BackdropMode.Solid,
                Padding = 16.0,
                DropShadow = true,
            },
        },
        new()
        {
            Id = "dark-navy",
            Name = "ダーク・ネイビー",
            Category = "ダーク",
            Theme = new WidgetTheme
            {
                FontFamily = "Segoe UI Variable Display",
                FontScale = 1.0,
                FontWeight = 500,
                TintColor = "#FFEDEFF7",
                AccentColor = "#FF5CC8FF",
                SecondaryColor = "#FF98A2C0",
                BackgroundColor = "#F0141A2E",
                BorderColor = "#FF29314E",
                BorderThickness = 1.0,
                CornerRadius = 14.0,
                Backdrop = BackdropMode.Solid,
                Padding = 16.0,
                DropShadow = true,
            },
        },
        new()
        {
            Id = "dark-mica",
            Name = "ダーク・ミカ",
            Category = "ダーク",
            Theme = new WidgetTheme
            {
                FontFamily = "Segoe UI Variable Display",
                FontScale = 1.0,
                FontWeight = 400,
                TintColor = "#FFFFFFFF",
                AccentColor = "#FF60CDFF",
                SecondaryColor = "#FFB8B8B8",
                BackgroundColor = "#C01B1B1F",
                BorderColor = "#FF333333",
                BorderThickness = 1.0,
                CornerRadius = 18.0,
                Backdrop = BackdropMode.Mica,
                Padding = 16.0,
                DropShadow = true,
            },
        },
        new()
        {
            Id = "dark-carbon",
            Name = "ダーク・カーボン",
            Category = "ダーク",
            Theme = new WidgetTheme
            {
                FontFamily = "Cascadia Mono",
                FontScale = 1.0,
                FontWeight = 500,
                TintColor = "#FFFFFFFF",
                AccentColor = "#FFE0A73E",
                SecondaryColor = "#FFACACAC",
                BackgroundColor = "#B01A1A1A",
                BorderColor = "#FF3A3A3A",
                BorderThickness = 1.0,
                CornerRadius = 10.0,
                Backdrop = BackdropMode.Frosted,
                Padding = 14.0,
                DropShadow = true,
            },
        },

        // ---- ライト --------------------------------------------------------
        new()
        {
            Id = "light-cloud",
            Name = "ライト・クラウド",
            Category = "ライト",
            Theme = new WidgetTheme
            {
                FontFamily = "Segoe UI Variable Display",
                FontScale = 1.0,
                FontWeight = 400,
                TintColor = "#FF1F1F1F",
                AccentColor = "#FF0F6CBD",
                SecondaryColor = "#FF5B5B5B",
                BackgroundColor = "#F5FBFBFC",
                BorderColor = "#FFE1E1E1",
                BorderThickness = 1.0,
                CornerRadius = 16.0,
                Backdrop = BackdropMode.Solid,
                Padding = 16.0,
                DropShadow = true,
            },
        },
        new()
        {
            Id = "light-linen",
            Name = "ライト・リネン",
            Category = "ライト",
            Theme = new WidgetTheme
            {
                FontFamily = "Georgia",
                FontScale = 1.0,
                FontWeight = 400,
                TintColor = "#FF3A2E22",
                AccentColor = "#FFB5651D",
                SecondaryColor = "#FF7A6B58",
                BackgroundColor = "#F0FBF7EF",
                BorderColor = "#FFE7DCC9",
                BorderThickness = 1.0,
                CornerRadius = 12.0,
                Backdrop = BackdropMode.Solid,
                Padding = 16.0,
                DropShadow = false,
            },
        },
        new()
        {
            Id = "light-sky-frosted",
            Name = "ライト・スカイ",
            Category = "ライト",
            Theme = new WidgetTheme
            {
                FontFamily = "Segoe UI",
                FontScale = 1.0,
                FontWeight = 400,
                TintColor = "#FF0B3556",
                AccentColor = "#FF1E88E5",
                SecondaryColor = "#FF4C6B82",
                BackgroundColor = "#B0EAF4FF",
                BorderColor = "#FFCFE6FB",
                BorderThickness = 1.0,
                CornerRadius = 20.0,
                Backdrop = BackdropMode.Frosted,
                Padding = 16.0,
                DropShadow = true,
            },
        },

        // ---- カラフル ------------------------------------------------------
        new()
        {
            Id = "colorful-sunset",
            Name = "カラフル・サンセット",
            Category = "カラフル",
            Theme = new WidgetTheme
            {
                FontFamily = "Segoe UI Variable Display",
                FontScale = 1.0,
                FontWeight = 600,
                TintColor = "#FFFFFFFF",
                AccentColor = "#FFFFD166",
                SecondaryColor = "#FFFFD9C7",
                BackgroundColor = "#FFC1440E",
                BorderColor = "#FF8C2F08",
                BorderThickness = 1.0,
                CornerRadius = 22.0,
                Backdrop = BackdropMode.Solid,
                Padding = 16.0,
                DropShadow = true,
            },
        },
        new()
        {
            Id = "colorful-mint",
            Name = "カラフル・ミント",
            Category = "カラフル",
            Theme = new WidgetTheme
            {
                FontFamily = "Segoe UI Variable Display",
                FontScale = 1.0,
                FontWeight = 500,
                TintColor = "#FFFFFFFF",
                AccentColor = "#FFFFC857",
                SecondaryColor = "#FFB9E6C9",
                BackgroundColor = "#FF0F5132",
                BorderColor = "#FF0B3D25",
                BorderThickness = 1.0,
                CornerRadius = 24.0,
                Backdrop = BackdropMode.Solid,
                Padding = 18.0,
                DropShadow = true,
            },
        },
        new()
        {
            Id = "colorful-berry",
            Name = "カラフル・ベリー",
            Category = "カラフル",
            Theme = new WidgetTheme
            {
                FontFamily = "Comic Sans MS",
                FontScale = 1.0,
                FontWeight = 400,
                TintColor = "#FFFFFFFF",
                AccentColor = "#FFFF6FB5",
                SecondaryColor = "#FFE7B8D6",
                BackgroundColor = "#FF6A1B4D",
                BorderColor = "#FF3D0F2C",
                BorderThickness = 1.0,
                CornerRadius = 20.0,
                Backdrop = BackdropMode.Solid,
                Padding = 16.0,
                DropShadow = true,
            },
        },

        // ---- レトロ --------------------------------------------------------
        new()
        {
            Id = "retro-terminal",
            Name = "レトロ・ターミナル",
            Category = "レトロ",
            Theme = new WidgetTheme
            {
                FontFamily = "Consolas",
                FontScale = 1.0,
                FontWeight = 500,
                TintColor = "#FF33FF66",
                AccentColor = "#FFFFB000",
                SecondaryColor = "#FF1F8C3B",
                BackgroundColor = "#FF041B0A",
                BorderColor = "#FF0B3D1D",
                BorderThickness = 1.0,
                CornerRadius = 6.0,
                Backdrop = BackdropMode.Solid,
                Padding = 14.0,
                DropShadow = false,
            },
        },
        new()
        {
            Id = "retro-sunbeam",
            Name = "レトロ・サンビーム",
            Category = "レトロ",
            Theme = new WidgetTheme
            {
                FontFamily = "Georgia",
                FontScale = 1.0,
                FontWeight = 500,
                TintColor = "#FF6B3A1E",
                AccentColor = "#FFD4622B",
                SecondaryColor = "#FF9C7A54",
                BackgroundColor = "#FFF4E3C1",
                BorderColor = "#FFD9B98A",
                BorderThickness = 1.0,
                CornerRadius = 18.0,
                Backdrop = BackdropMode.Solid,
                Padding = 16.0,
                DropShadow = true,
            },
        },
        new()
        {
            Id = "retro-diner",
            Name = "レトロ・ダイナー",
            Category = "レトロ",
            Theme = new WidgetTheme
            {
                FontFamily = "Times New Roman",
                FontScale = 1.0,
                FontWeight = 500,
                TintColor = "#FFFCEEDD",
                AccentColor = "#FFE8B23D",
                SecondaryColor = "#FFE0A9A9",
                BackgroundColor = "#FF7A1F2B",
                BorderColor = "#FF4A0F17",
                BorderThickness = 1.0,
                CornerRadius = 14.0,
                Backdrop = BackdropMode.Solid,
                Padding = 16.0,
                DropShadow = true,
            },
        },
    ];
}
