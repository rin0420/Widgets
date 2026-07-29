using Microsoft.UI.Xaml;
using Widgets.App.Interop;
using Widgets.App.Models;
using Widgets.App.Services;

namespace Widgets.App.Hosting;

/// <summary>
/// Owns the live desktop windows. The store is the single source of truth: this class only
/// translates store events into window lifetimes, and window interactions back into store writes.
/// </summary>
public sealed class WidgetHostManager
{
    private readonly Dictionary<string, WidgetHostWindow> _hosts = new();

    private DispatcherTimer? _fullscreenTimer;
    private bool _started;
    private bool _hiddenForFullscreen;

    /// <summary>Set while pushing a host-originated change into the store, so the echo is ignored.</summary>
    private string? _selfUpdateId;

    /// <summary>Raised when a widget asks to be edited; the shell opens the editor.</summary>
    public event EventHandler<WidgetDefinition>? EditRequested;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;

        var store = AppServices.Store;
        store.WidgetAdded += OnWidgetAdded;
        store.WidgetChanged += OnWidgetChanged;
        store.WidgetRemoved += OnWidgetRemoved;
        store.SettingsChanged += OnSettingsChanged;

        foreach (var def in store.Document.Widgets.Where(w => w.IsVisible).ToList())
        {
            Create(def);
        }

        _fullscreenTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _fullscreenTimer.Tick += OnFullscreenTick;
        _fullscreenTimer.Start();
    }

    public void Stop()
    {
        if (!_started)
        {
            return;
        }

        _started = false;

        var store = AppServices.Store;
        store.WidgetAdded -= OnWidgetAdded;
        store.WidgetChanged -= OnWidgetChanged;
        store.WidgetRemoved -= OnWidgetRemoved;
        store.SettingsChanged -= OnSettingsChanged;

        if (_fullscreenTimer is not null)
        {
            _fullscreenTimer.Stop();
            _fullscreenTimer.Tick -= OnFullscreenTick;
            _fullscreenTimer = null;
        }

        foreach (var host in _hosts.Values.ToList())
        {
            CloseHost(host);
        }

        _hosts.Clear();
        _hiddenForFullscreen = false;
    }

    /// <summary>Creates, updates or tears down the window for <paramref name="def"/> to match its state.</summary>
    public void Refresh(WidgetDefinition def)
    {
        AppServices.OnUi(() =>
        {
            try
            {
                if (!def.IsVisible)
                {
                    Remove(def.Id);
                    return;
                }

                if (_hosts.TryGetValue(def.Id, out var host))
                {
                    host.ApplyDefinition(def);
                }
                else
                {
                    Create(def);
                }
            }
            catch (Exception ex)
            {
                Crash.Log(ex, nameof(Refresh));
            }
        });
    }

    public void Remove(string id)
    {
        AppServices.OnUi(() =>
        {
            try
            {
                if (_hosts.Remove(id, out var host))
                {
                    CloseHost(host);
                }
            }
            catch (Exception ex)
            {
                Crash.Log(ex, nameof(Remove));
            }
        });
    }

    public void ShowEditor(WidgetDefinition def) => EditRequested?.Invoke(this, def);

    private void Create(WidgetDefinition def)
    {
        var host = new WidgetHostWindow(def);

        host.EditRequested += OnHostEditRequested;
        host.DuplicateRequested += OnHostDuplicateRequested;
        host.DeleteRequested += OnHostDeleteRequested;
        host.DefinitionChanged += OnHostDefinitionChanged;
        host.PositionChanged += OnHostPositionChanged;

        _hosts[def.Id] = host;

        host.ShowWidget();

        if (_hiddenForFullscreen)
        {
            host.SetHidden(true);
        }
    }

    private void CloseHost(WidgetHostWindow host)
    {
        host.EditRequested -= OnHostEditRequested;
        host.DuplicateRequested -= OnHostDuplicateRequested;
        host.DeleteRequested -= OnHostDeleteRequested;
        host.DefinitionChanged -= OnHostDefinitionChanged;
        host.PositionChanged -= OnHostPositionChanged;
        host.Close();
    }

    // ---- Store -> windows ------------------------------------------------------

    private void OnWidgetAdded(object? sender, WidgetDefinition def) => Refresh(def);

    private void OnWidgetChanged(object? sender, WidgetDefinition def)
    {
        if (def.Id == _selfUpdateId)
        {
            return;
        }

        Refresh(def);
    }

    private void OnWidgetRemoved(object? sender, string id) => Remove(id);

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        AppServices.OnUi(() =>
        {
            try
            {
                if (!AppServices.Store.Document.Settings.HideOnFullscreen && _hiddenForFullscreen)
                {
                    SetAllHidden(false);
                }
            }
            catch (Exception ex)
            {
                Crash.Log(ex, nameof(OnSettingsChanged));
            }
        });
    }

    // ---- Windows -> store ------------------------------------------------------

    private void OnHostEditRequested(object? sender, WidgetDefinition def) => ShowEditor(def);

    private void OnHostDuplicateRequested(object? sender, WidgetDefinition def)
    {
        try
        {
            var copy = def.Clone();
            copy.Id = Guid.NewGuid().ToString("N");
            copy.X = def.X + 24;
            copy.Y = def.Y + 24;
            AppServices.Store.Add(copy);
        }
        catch (Exception ex)
        {
            Crash.Log(ex, nameof(OnHostDuplicateRequested));
        }
    }

    private void OnHostDeleteRequested(object? sender, WidgetDefinition def)
    {
        try
        {
            AppServices.Store.Remove(def.Id);
        }
        catch (Exception ex)
        {
            Crash.Log(ex, nameof(OnHostDeleteRequested));
        }
    }

    private void OnHostDefinitionChanged(object? sender, WidgetDefinition def) => PushUpdate(def);

    private void OnHostPositionChanged(object? sender, WidgetDefinition def) => PushUpdate(def);

    private void PushUpdate(WidgetDefinition def)
    {
        try
        {
            _selfUpdateId = def.Id;
            AppServices.Store.Update(def);
        }
        catch (Exception ex)
        {
            Crash.Log(ex, nameof(PushUpdate));
        }
        finally
        {
            _selfUpdateId = null;
        }
    }

    // ---- Full-screen auto-hide -------------------------------------------------

    private void OnFullscreenTick(object? sender, object e)
    {
        try
        {
            if (!AppServices.Store.Document.Settings.HideOnFullscreen)
            {
                return;
            }

            var shouldHide = WindowHelper.IsForegroundFullscreen();
            if (shouldHide != _hiddenForFullscreen)
            {
                SetAllHidden(shouldHide);
            }
        }
        catch (Exception ex)
        {
            Crash.Log(ex, nameof(OnFullscreenTick));
        }
    }

    private void SetAllHidden(bool hidden)
    {
        _hiddenForFullscreen = hidden;

        foreach (var host in _hosts.Values)
        {
            host.SetHidden(hidden);
        }
    }
}
