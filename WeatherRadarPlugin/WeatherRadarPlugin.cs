using System;
using System.ComponentModel.Composition;
using System.Drawing;
using System.Windows.Forms;
using vatsys;
using vatsys.Plugin;
using VATSYSControls;

namespace WeatherRadarPlugin;

/// <summary>
/// vatSys entry point and Tools-menu surface.
///
/// vatSys composes plugins through MEF and only guards that call against
/// CompositionException, so a constructor that throws can take the client down before it
/// starts. Every menu event is likewise raised straight from the WinForms message loop.
/// Nothing here is allowed to throw: construction degrades to a disabled menu, and each
/// handler routes its own faults into the vatSys error list.
/// </summary>
[Export(typeof(IPlugin))]
public sealed class WeatherRadarPlugin : IPlugin, IDisposable
{
    private const string PluginName = "Weather Radar Plugin";
    private const int DefaultTransparencyPercent = 80;

    private readonly RadarOverlayService? _overlay;
    private readonly ToolStripMenuItem? _rootItem;
    private readonly ToolStripMenuItem? _showItem;
    private readonly ToolStripMenuItem? _animateItem;
    private readonly ToolStripMenuItem? _pauseItem;
    private readonly ToolStripMenuItem? _transparencyLabel;
    private readonly Slider? _transparencySlider;
    private readonly System.Windows.Forms.Timer? _transparencyDebounce;
    private readonly CustomToolStripMenuItem? _menuItem;
    private bool _disposed;

    public WeatherRadarPlugin()
    {
        try
        {
            _overlay = new RadarOverlayService();
            _showItem = new ToolStripMenuItem("Show All Local Radars")
            {
                CheckOnClick = true,
                ToolTipText = "Display precipitation from all available BOM 128 km local radars"
            };
            _animateItem = new ToolStripMenuItem("Animate Recent Scans")
            {
                CheckOnClick = true,
                Enabled = false,
                ToolTipText = "Loop genuine BOM observations cached since vatSys started"
            };
            _pauseItem = new ToolStripMenuItem("Pause Animation")
            {
                CheckOnClick = true,
                Enabled = false,
                ToolTipText = "Freeze the animation on its current observation"
            };
            _transparencyLabel = new ToolStripMenuItem($"Transparency: {DefaultTransparencyPercent}%")
            {
                Enabled = false
            };
            _transparencySlider = BuildSlider();
            _transparencyDebounce = new System.Windows.Forms.Timer { Interval = 200 };
            _transparencyDebounce.Tick += TransparencyDebounceTick;

            _showItem.CheckedChanged += ShowItemCheckedChanged;
            _animateItem.CheckedChanged += AnimateItemCheckedChanged;
            _pauseItem.CheckedChanged += PauseItemCheckedChanged;
            _transparencySlider.ValueChanged += TransparencySliderValueChanged;

            _rootItem = new ToolStripMenuItem("Weather Radar");
            _rootItem.DropDownItems.Add(_showItem);
            _rootItem.DropDownItems.Add(new ToolStripSeparator());
            _rootItem.DropDownItems.Add(_animateItem);
            _rootItem.DropDownItems.Add(_pauseItem);
            _rootItem.DropDownItems.Add(new ToolStripSeparator());
            _rootItem.DropDownItems.Add(_transparencyLabel);
            _rootItem.DropDownItems.Add(new ToolStripControlHost(_transparencySlider)
            {
                AutoSize = false,
                Width = 220,
                Height = 52,
                Margin = new Padding(4, 0, 4, 4)
            });

            _menuItem = new CustomToolStripMenuItem(
                CustomToolStripMenuItemWindowType.Main,
                CustomToolStripMenuItemCategory.Tools,
                _rootItem);
            MMI.AddCustomMenuItem(_menuItem);
        }
        catch (Exception ex)
        {
            // vatSys must still start. Leave the plugin inert and surface the reason.
            _disposed = true;
            ReportSafely(ex);
        }
    }

    public string Name => PluginName;

    public void OnFDRUpdate(FDP2.FDR updated) { }

    public void OnRadarTrackUpdate(RDP.RadarTrack updated) { }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Guard(() =>
        {
            if (_showItem is not null) _showItem.CheckedChanged -= ShowItemCheckedChanged;
            if (_animateItem is not null) _animateItem.CheckedChanged -= AnimateItemCheckedChanged;
            if (_pauseItem is not null) _pauseItem.CheckedChanged -= PauseItemCheckedChanged;
            if (_transparencySlider is not null) _transparencySlider.ValueChanged -= TransparencySliderValueChanged;
        });
        Guard(() =>
        {
            if (_transparencyDebounce is null) return;
            _transparencyDebounce.Stop();
            _transparencyDebounce.Tick -= TransparencyDebounceTick;
            _transparencyDebounce.Dispose();
        });
        Guard(() => { if (_menuItem is not null) MMI.RemoveCustomMenuItem(_menuItem); });
        Guard(() => _overlay?.Dispose());
        Guard(() => _rootItem?.Dispose());
    }

    private Slider BuildSlider()
    {
        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = DefaultTransparencyPercent,
            AutoSize = false,
            Width = 210,
            Height = 48,
            Cursor = Cursors.NoMoveHoriz
        };
        // Theme lookups touch vatSys colour and font state that may not be loaded yet;
        // the slider is perfectly usable with WinForms defaults if any of them fail.
        Guard(() => slider.BackColor = Colours.GetColour(Colours.Identities.WindowBackground));
        Guard(() => slider.ForeColor = Colours.GetColour(Colours.Identities.WindowButtonDepressed));
        Guard(() => slider.FontColor = Colours.GetColour(Colours.Identities.InteractiveText));
        Guard(() => { if (MMI.eurofont_winsml is Font font) slider.Font = font; });
        return slider;
    }

    private async void ShowItemCheckedChanged(object? sender, EventArgs e)
    {
        // async void: an escaping exception here has no caller to observe it and would be
        // raised on the GUI thread, so the whole body is guarded.
        if (_disposed || _showItem is null || _overlay is null) return;
        var wanted = _showItem.Checked;
        Guard(() => _showItem.Enabled = false);
        try
        {
            await _overlay.SetEnabledAsync(wanted);
            if (_disposed) return;
            if (_animateItem is not null) _animateItem.Enabled = wanted;
            if (!wanted)
            {
                SetCheckedWithoutEvent(_animateItem, false, AnimateItemCheckedChanged);
                SetCheckedWithoutEvent(_pauseItem, false, PauseItemCheckedChanged);
                if (_pauseItem is not null) _pauseItem.Enabled = false;
            }
        }
        catch (Exception ex)
        {
            if (_disposed) return;
            Guard(() =>
            {
                SetCheckedWithoutEvent(_showItem, false, ShowItemCheckedChanged);
                if (_animateItem is not null) _animateItem.Enabled = false;
            });
            ReportSafely(ex);
        }
        finally
        {
            if (!_disposed) Guard(() => _showItem.Enabled = true);
        }
    }

    private void AnimateItemCheckedChanged(object? sender, EventArgs e) => Guard(() =>
    {
        if (_disposed || _animateItem is null || _overlay is null) return;
        _overlay.SetAnimationEnabled(_animateItem.Checked);
        if (_pauseItem is not null) _pauseItem.Enabled = _animateItem.Checked;
        if (!_animateItem.Checked)
            SetCheckedWithoutEvent(_pauseItem, false, PauseItemCheckedChanged);
    });

    private void PauseItemCheckedChanged(object? sender, EventArgs e) => Guard(() =>
    {
        if (_disposed || _pauseItem is null || _overlay is null) return;
        _overlay.SetPaused(_pauseItem.Checked);
    });

    private void TransparencySliderValueChanged(object? sender, EventArgs e) => Guard(() =>
    {
        if (_disposed || _transparencySlider is null) return;
        if (_transparencyLabel is not null)
            _transparencyLabel.Text = $"Transparency: {_transparencySlider.Value}%";
        // Dragging raises this continuously; each apply repaints every ASD, so coalesce.
        _transparencyDebounce?.Stop();
        _transparencyDebounce?.Start();
    });

    private void TransparencyDebounceTick(object? sender, EventArgs e) => Guard(() =>
    {
        _transparencyDebounce?.Stop();
        if (_disposed || _transparencySlider is null || _overlay is null) return;
        _overlay.SetTransparency(_transparencySlider.Value);
    });

    private static void SetCheckedWithoutEvent(ToolStripMenuItem? item, bool value, EventHandler handler)
    {
        if (item is null) return;
        item.CheckedChanged -= handler;
        item.Checked = value;
        item.CheckedChanged += handler;
    }

    private static void Guard(Action action)
    {
        try { action(); }
        catch (Exception ex) { ReportSafely(ex); }
    }

    private static void ReportSafely(Exception exception)
    {
        try { Errors.Add(exception, PluginName); }
        catch { /* Reporting a fault must never raise one. */ }
    }
}
