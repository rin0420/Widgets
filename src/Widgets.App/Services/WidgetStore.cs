using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Widgets.App.Models;

namespace Widgets.App.Services;

/// <summary>
/// Owns the single on-disk document (widgets + settings + custom presets), and is the one
/// place that mutates it so every mutation can fan out a change event and a debounced save.
/// </summary>
public sealed class WidgetStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Lock _gate = new();
    private Timer? _saveTimer;

    public WidgetDocument Document { get; private set; } = new();

    public IReadOnlyList<ThemePreset> AllPresets
        => ThemePresetCatalog.BuiltIn.Concat(Document.CustomPresets).ToList();

    public event EventHandler<WidgetDefinition>? WidgetAdded;
    public event EventHandler<WidgetDefinition>? WidgetChanged;
    public event EventHandler<string>? WidgetRemoved;
    public event EventHandler? SettingsChanged;

    public async Task LoadAsync()
    {
        try
        {
            Paths.EnsureCreated();
            if (File.Exists(Paths.DocumentPath))
            {
                var json = await File.ReadAllTextAsync(Paths.DocumentPath).ConfigureAwait(false);
                var doc = JsonSerializer.Deserialize<WidgetDocument>(json, JsonOptions);
                if (doc is not null)
                {
                    Document = doc;
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Crash.Log(ex, "WidgetStore.LoadAsync");
        }

        Document = SeedDefaults();
    }

    /// <summary>Synchronous, atomic save — used on shutdown where there is no time for a debounce.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            try
            {
                Paths.EnsureCreated();
                var json = JsonSerializer.Serialize(Document, JsonOptions);
                var tmpPath = Paths.DocumentPath + ".tmp";
                File.WriteAllText(tmpPath, json);

                if (File.Exists(Paths.DocumentPath))
                {
                    File.Replace(tmpPath, Paths.DocumentPath, null);
                }
                else
                {
                    File.Move(tmpPath, Paths.DocumentPath, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                Crash.Log(ex, "WidgetStore.Flush");
            }
        }
    }

    public void RequestSave()
    {
        lock (_gate)
        {
            _saveTimer ??= new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
            _saveTimer.Change(TimeSpan.FromMilliseconds(600), Timeout.InfiniteTimeSpan);
        }
    }

    public void Add(WidgetDefinition def)
    {
        Document.Widgets.Add(def);
        WidgetAdded?.Invoke(this, def);
        RequestSave();
    }

    public void Remove(string id)
    {
        if (Document.Widgets.RemoveAll(w => w.Id == id) > 0)
        {
            WidgetRemoved?.Invoke(this, id);
            RequestSave();
        }
    }

    public void Update(WidgetDefinition def)
    {
        var index = Document.Widgets.FindIndex(w => w.Id == def.Id);
        if (index >= 0)
        {
            Document.Widgets[index] = def;
        }
        else
        {
            Document.Widgets.Add(def);
        }

        WidgetChanged?.Invoke(this, def);
        RequestSave();
    }

    public WidgetDefinition? Find(string id)
        => Document.Widgets.FirstOrDefault(w => w.Id == id);

    public void NotifySettingsChanged()
    {
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        RequestSave();
    }

    public void SaveCustomPreset(string name, WidgetTheme theme)
    {
        Document.CustomPresets.Add(new ThemePreset
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Category = "カスタム",
            Theme = theme.Clone(),
            IsBuiltIn = false,
        });

        SettingsChanged?.Invoke(this, EventArgs.Empty);
        RequestSave();
    }

    public void DeleteCustomPreset(string id)
    {
        if (Document.CustomPresets.RemoveAll(p => p.Id == id) > 0)
        {
            SettingsChanged?.Invoke(this, EventArgs.Empty);
            RequestSave();
        }
    }

    public void AddSavedColor(string hex)
    {
        var colors = Document.Settings.SavedColors;
        colors.RemoveAll(c => string.Equals(c, hex, StringComparison.OrdinalIgnoreCase));
        colors.Insert(0, hex);
        while (colors.Count > AppSettings.MaxSavedColors)
        {
            colors.RemoveAt(colors.Count - 1);
        }

        NotifySettingsChanged();
    }

    /// <summary>A pleasant starter set shown the very first time the app runs, or after a corrupt save.</summary>
    private static WidgetDocument SeedDefaults()
    {
        var doc = new WidgetDocument();

        // Stack the starter widgets down the right edge of the primary monitor. Positions are
        // physical pixels, so they have to be derived from the actual screen and DPI rather than
        // hardcoded — a 4K or scaled display would otherwise place them off-screen.
        var scale = GetDpiForSystem() / 96.0;
        var screenWidth = GetSystemMetrics(SM_CXSCREEN);
        var screenHeight = GetSystemMetrics(SM_CYSCREEN);
        const double Margin = 24;

        var cursorY = Margin * scale;

        WidgetDefinition Place(WidgetKind kind, WidgetSize size, int presetIndex)
        {
            var def = WidgetCatalog.CreateDefinition(kind, size);
            def.Theme = ThemePresetCatalog.BuiltIn[presetIndex].Theme.Clone();

            var (w, h) = WidgetMetrics.GetSize(size);
            def.X = (int)Math.Max(Margin * scale, screenWidth - (w + Margin) * scale);
            def.Y = (int)cursorY;

            cursorY += (h + 16) * scale;
            doc.Widgets.Add(def);
            return def;
        }

        Place(WidgetKind.DigitalClock, WidgetSize.Large, 3);
        Place(WidgetKind.Weather, WidgetSize.Medium, 7);
        Place(WidgetKind.SystemMonitor, WidgetSize.Small, 6);

        // On a short screen the stack can overflow; pull anything past the bottom back into view.
        foreach (var def in doc.Widgets)
        {
            var (_, h) = WidgetMetrics.GetSize(def.Size);
            def.Y = (int)Math.Min(def.Y, Math.Max(0, screenHeight - (h + Margin) * scale));
        }

        return doc;
    }

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
}
