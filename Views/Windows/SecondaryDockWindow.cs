using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenTaskbar.Models;
using OpenTaskbar.Services;

namespace OpenTaskbar;

/// <summary>
/// A lightweight dock for a non-primary display. It uses the strip Explorer's taskbar still reserves when there is
/// one, and reserves its own app-bar shelf when taskbar replacement released that strip. It lists the windows on
/// its display and unregisters the shelf when it closes.
/// </summary>
public sealed class SecondaryDockWindow : Window
{
    private const uint AbmNew = 0x00000000;
    private const uint AbmRemove = 0x00000001;
    private const uint AbmQueryPos = 0x00000002;
    private const uint AbmSetPos = 0x00000003;
    private const uint AbeBottom = 3;

    private sealed class AppSlot(string path, BitmapSource? icon, nint targetHandle, string title, bool isForeground, System.Windows.Shapes.Ellipse dot)
    {
        public string Path { get; } = path;
        public BitmapSource? Icon { get; } = icon;
        public nint TargetHandle { get; set; } = targetHandle;
        public string Title { get; set; } = title;
        public bool IsForeground { get; set; } = isForeground;
        public System.Windows.Shapes.Ellipse Dot { get; } = dot;
        public IReadOnlyList<TrackedWindow> Windows { get; set; } = [];
    }

    private readonly nint _monitor;
    private readonly StackPanel _apps;
    private readonly TextBlock _clock;
    private readonly Button _clockButton;
    private readonly Button _quickSettingsButton;
    private readonly StackPanel _widgetsArea;
    private readonly Border _surface;
    private readonly RunningAppIconCache _icons;
    private readonly List<AppSlot> _slots = [];
    private SecondaryAppScope _scope;
    private double _shelfHeight = DockMetrics.ShelfHeight;
    private string _lastClockText = string.Empty;
    private nint _handle;
    private bool _stripAvailable = true;
    private bool _fullScreen;
    private bool _appBarRegistered;
    private (int Left, int Right, int Height)? _reservation;

    private void ApplyVisibility() => Visibility = _stripAvailable && !_fullScreen ? Visibility.Visible : Visibility.Hidden;

    public SecondaryDockWindow(nint monitor, RunningAppIconCache iconCache, SecondaryAppScope scope = SecondaryAppScope.CurrentMonitorOnly, bool showWidgets = true)
    {
        _monitor = monitor;
        _icons = iconCache;
        _scope = scope;
        Title = UiText.Get("SecondaryDockTitle");
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SetResourceReference(FontFamilyProperty, "InterfaceFont");

        _apps = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _clock = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0), FontSize = 12, FontWeight = FontWeights.SemiBold };
        _clock.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");

        _clockButton = DockFlyout.FlatButton(_clock, (_, _) => ShellShortcuts.OpenNotificationCenter(), UiText.Get("NotificationCenter"));
        _clockButton.Padding = new Thickness(6, 2, 6, 2);

        var quickIcon = new ContentControl { Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center };
        quickIcon.SetResourceReference(ContentControl.ContentProperty, "IconVolume");
        quickIcon.SetResourceReference(StyleProperty, "WidgetIcon");
        _quickSettingsButton = DockFlyout.FlatButton(quickIcon, (_, _) => ShellShortcuts.OpenQuickSettings(), UiText.Get("WidgetQuickSettings"));
        _quickSettingsButton.Padding = new Thickness(6, 2, 6, 2);
        _quickSettingsButton.Visibility = showWidgets ? Visibility.Visible : Visibility.Collapsed;

        _widgetsArea = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        _widgetsArea.Children.Add(_quickSettingsButton);
        _widgetsArea.Children.Add(_clockButton);

        var row = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(_widgetsArea, Dock.Right);
        row.Children.Add(_widgetsArea);
        row.Children.Add(_apps);
        var surface = new Border
        {
            Child = row,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(6, 3, 6, 3)
        };
        surface.SetResourceReference(Border.BackgroundProperty, "DockMaterial");
        surface.SetResourceReference(Border.BorderBrushProperty, "DockBorderMaterial");
        surface.ContextMenu = BuildContextMenu();
        _surface = surface;
        Content = surface;
        UiText.Current.PropertyChanged += OnLanguageChanged;
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        var settingsItem = new MenuItem { Header = UiText.Get("Settings") };
        settingsItem.Click += (_, _) => (Application.Current?.MainWindow as MainWindow)?.OpenSettingsFromSecondaryDock();
        var exitItem = new MenuItem { Header = UiText.Get("Exit") };
        exitItem.Click += (_, _) => Application.Current?.Shutdown();
        menu.Items.Add(settingsItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);
        return menu;
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        Title = UiText.Get("SecondaryDockTitle");
        var notifLabel = UiText.Get("NotificationCenter");
        _clockButton.ToolTip = notifLabel;
        System.Windows.Automation.AutomationProperties.SetName(_clockButton, notifLabel);
        var quickLabel = UiText.Get("WidgetQuickSettings");
        _quickSettingsButton.ToolTip = quickLabel;
        System.Windows.Automation.AutomationProperties.SetName(_quickSettingsButton, quickLabel);
    }

    public nint Monitor => _monitor;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _handle = new WindowInteropHelper(this).Handle;
        MainWindow.ExcludeFromTaskSwitcher(_handle);
        WindowThumbnailPreview.ExcludeFromPeek(_handle);
        // Tool window + no-activate: out of Alt+Tab, out of the running-app list, and clicks never steal focus.
        var style = GetWindowLongPtr(_handle, -20).ToInt64();
        SetWindowLongPtr(_handle, -20, new nint((style | 0x80 | 0x08000000) & ~0x40000));
        HwndSource.FromHwnd(_handle)?.AddHook((nint _, int message, nint _, nint _, ref bool handled) =>
        {
            if (message != 0x0021) return 0; // WM_MOUSEACTIVATE
            handled = true;
            return 3; // MA_NOACTIVATE
        });
        RegisterAppBar();
        PositionOnMonitor();
        if (TaskbarReplacementSession.IsAnySessionActive)
        {
            TaskbarReplacementSession.EnforceSecondaryTraysHidden();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        UiText.Current.PropertyChanged -= OnLanguageChanged;
        UnregisterAppBar();
        base.OnClosed(e);
    }

    /// <summary>
    /// Sits on the strip below the work area. When taskbar replacement releases Explorer's secondary taskbar strip,
    /// reserves an app-bar shelf of its own so the dock stays visible without covering maximized windows.
    /// </summary>
    public void PositionOnMonitor()
    {
        if (_handle == 0) return;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(_monitor, ref info)) return;
        var dpi = GetDpiForMonitor(_monitor);
        // The work-area strip includes our own app-bar reservation. Subtract it before deciding
        // whether Explorer has reserved enough room, otherwise we release and reclaim our
        // shelf on alternating work-area notifications.
        var stripHeight = Math.Max(0, info.Monitor.Bottom - info.Work.Bottom);
        int height;

        var metrics = DockLayoutMetrics.Calculate(_shelfHeight, 0, 0, dpi);
        var additional = MissingShelfPixels(stripHeight, _reservation?.Height ?? 0, dpi, _shelfHeight);
        if (additional == 0)
        {
            // Explorer still reserves enough space here: reuse it and drop any shelf we reserved earlier.
            if (_reservation is not null)
            {
                ReleaseReservation();
                // Re-read the work area after removing our own shelf.
                if (!GetMonitorInfo(_monitor, ref info)) return;
                stripHeight = Math.Max(0, info.Monitor.Bottom - info.Work.Bottom);
            }
            height = stripHeight;
        }
        else
        {
            // The secondary taskbar is hidden and its strip released. Reserve as much as is missing, just above
            // whatever strip remains, so the dock is visible and maximized windows still avoid it.
            if (_appBarRegistered && _reservation != (info.Monitor.Left, info.Monitor.Right, additional))
            {
                var (top, bottom) = ComputeReservationBounds(info.Monitor.Bottom, stripHeight, _reservation?.Height ?? 0, additional);
                var data = CreateData();
                data.uEdge = AbeBottom;
                data.rc = new NativeRect
                {
                    Left = info.Monitor.Left,
                    Top = top,
                    Right = info.Monitor.Right,
                    Bottom = bottom
                };
                SHAppBarMessage(AbmQueryPos, ref data);
                data.rc.Bottom = Math.Min(data.rc.Bottom, bottom);
                data.rc.Top = data.rc.Bottom - additional;
                if (SHAppBarMessage(AbmSetPos, ref data) != 0)
                    _reservation = (info.Monitor.Left, info.Monitor.Right, additional);
                else
                    AppLog.Warning("Could not reserve the secondary dock shelf.");
            }

            // Re-read the work area: the reservation may have moved the bottom edge.
            if (!GetMonitorInfo(_monitor, ref info))
                height = metrics.ShelfPixels;
            else
                height = Math.Max(metrics.ShelfPixels, info.Monitor.Bottom - info.Work.Bottom);
        }

        _stripAvailable = true;
        ApplyVisibility();
        SetWindowPos(_handle, new nint(-1), info.Monitor.Left, info.Monitor.Bottom - height,
            info.Monitor.Right - info.Monitor.Left, height, PositionFlags(_stripAvailable && !_fullScreen));
    }

    /// <summary>True when Explorer released the secondary taskbar strip and the dock must reserve its own shelf.</summary>
    internal static bool NeedsOwnShelf(int stripHeight, uint dpi, double shelfHeight = DockMetrics.ShelfHeight) =>
        MissingShelfPixels(stripHeight, 0, dpi, shelfHeight) > 0;

    // A monitor's work-area strip contains both the Explorer reservation and our own shelf.
    // Only the external portion should determine whether our shelf is still needed and where our band sits.
    internal static int ExternalStripPixels(int totalStripHeight, int ownReservedHeight) =>
        Math.Max(0, totalStripHeight - Math.Max(0, ownReservedHeight));

    internal static (int Top, int Bottom) ComputeReservationBounds(int monitorBottom, int totalStripHeight, int ownReservedHeight, int additionalHeight)
    {
        var bottom = monitorBottom - ExternalStripPixels(totalStripHeight, ownReservedHeight);
        return (bottom - Math.Max(0, additionalHeight), bottom);
    }

    internal static int MissingShelfPixels(int totalStripHeight, int ownReservedHeight, uint dpi, double shelfHeight = DockMetrics.ShelfHeight) =>
        DockLayoutMetrics.Calculate(shelfHeight, 0, 0, dpi)
            .AdditionalReservation(ExternalStripPixels(totalStripHeight, ownReservedHeight), false);

    // A hidden fullscreen dock must not be re-shown by a subsequent position refresh.
    internal static uint PositionFlags(bool visible) => 0x0010u | (visible ? 0x0040u : 0u);

    private AppBarData CreateData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<AppBarData>(),
        hWnd = _handle
    };

    private void RegisterAppBar()
    {
        var data = CreateData();
        _appBarRegistered = SHAppBarMessage(AbmNew, ref data) != 0;
        if (!_appBarRegistered)
            AppLog.Warning("Could not register a secondary dock as an app bar; the dock will use the taskbar strip.");
    }

    private void UnregisterAppBar()
    {
        if (!_appBarRegistered) return;
        var data = CreateData();
        SHAppBarMessage(AbmRemove, ref data);
        _appBarRegistered = false;
        _reservation = null;
    }

    private void ReleaseReservation()
    {
        UnregisterAppBar();
        RegisterAppBar();
    }

    public void Configure(SecondaryAppScope scope, bool showWidgets)
    {
        _scope = scope;
        _quickSettingsButton.Visibility = showWidgets ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Current shelf in DIPs, derived from the active size + scale profile.</summary>
    public double ShelfHeight => _shelfHeight;

    /// <summary>Explicit visible bar height in DIPs; NaN keeps the surface content-sized.</summary>
    public double SurfaceHeight => _surface.Height;

    /// <summary>
    /// Applies the reserved shelf plus the visible bar height. Auto keeps the surface content-sized; fixed/Fill
    /// heights stretch it to match the primary dock (bottom-aligned, the top gap stays transparent above it).
    /// </summary>
    public void ApplyShelfHeight(double shelfHeight, double surfaceHeight = double.NaN)
    {
        _shelfHeight = shelfHeight;
        if (double.IsNaN(surfaceHeight))
        {
            _surface.Height = double.NaN;
            _surface.VerticalAlignment = VerticalAlignment.Stretch;
            _surface.Margin = new Thickness(6, 3, 6, 3);
        }
        else
        {
            _surface.Height = Math.Max(24, surfaceHeight);
            _surface.VerticalAlignment = VerticalAlignment.Bottom;
            _surface.Margin = new Thickness(6, 0, 6, 0);
        }
    }

    /// <summary>
    /// Consumes the same projected running apps as the primary dock, so a background window can never become an
    /// icon here while it is correctly hidden on the main display.
    /// </summary>
    public void Update(IReadOnlyList<RunningAppCandidate> apps, nint foreground)
    {
        var clockText = DateTime.Now.ToString("t", UiText.Current.Language.Culture);
        if (!string.Equals(clockText, _lastClockText, StringComparison.Ordinal))
        {
            _lastClockText = clockText;
            _clock.Text = clockText;
        }
        var mine = new List<(string Path, IReadOnlyList<TrackedWindow> Windows, BitmapSource? Icon)>();
        foreach (var app in apps)
        {
            var windows = _scope == SecondaryAppScope.AllMonitors
                ? app.Windows
                : app.Windows.Where(window => MonitorFromWindow(window.Handle, 2) == _monitor).ToList();
            if (windows.Count == 0) continue;
            mine.Add((app.ExecutablePath, windows, _icons.Get(app.ExecutablePath, windows[0].Handle)));
        }
        // Captionless windows covering the display (games, video) hide this dock like the main one.
        _fullScreen = foreground != 0 && MonitorFromWindow(foreground, 2) == _monitor && IsFullScreen(foreground);
        ApplyVisibility();

        var icons = mine.Select(entry => entry.Icon).ToList();
        var sameStructure = mine.Count == _slots.Count;
        if (sameStructure)
        {
            for (var i = 0; i < mine.Count; i++)
            {
                if (!string.Equals(mine[i].Path, _slots[i].Path, StringComparison.OrdinalIgnoreCase)
                    || !ReferenceEquals(icons[i], _slots[i].Icon))
                {
                    sameStructure = false;
                    break;
                }
            }
        }

        if (sameStructure)
        {
            for (var i = 0; i < mine.Count; i++)
            {
                var entry = mine[i];
                var slot = _slots[i];
                var first = entry.Windows[0];
                var active = entry.Windows.FirstOrDefault(window => window.Handle == foreground) ?? first;
                var isForeground = active.Handle == foreground;
                slot.TargetHandle = active.Handle;
                slot.Windows = entry.Windows;
                if (!string.Equals(slot.Title, first.Title, StringComparison.Ordinal))
                {
                    slot.Title = first.Title;
                    if (_apps.Children[i] is Button btn)
                    {
                        btn.ToolTip = first.Title;
                        System.Windows.Automation.AutomationProperties.SetName(btn, first.Title);
                    }
                }
                if (slot.IsForeground != isForeground)
                {
                    slot.IsForeground = isForeground;
                    slot.Dot.Width = isForeground ? 5 : 4;
                    slot.Dot.Height = isForeground ? 5 : 4;
                    slot.Dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentBrush");
                    slot.Dot.Opacity = isForeground ? 1.0 : 0.4;
                }
            }
            return;
        }

        _slots.Clear();
        _apps.Children.Clear();
        for (var index = 0; index < mine.Count; index++)
        {
            var entry = mine[index];
            var path = entry.Path;
            var icon = icons[index];
            var first = entry.Windows[0];
            var active = entry.Windows.FirstOrDefault(window => window.Handle == foreground) ?? first;
            var isForeground = active.Handle == foreground;
            var image = new Image { Source = icon, Width = 26, Height = 26 };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            var dot = new System.Windows.Shapes.Ellipse { Width = isForeground ? 5 : 4, Height = isForeground ? 5 : 4, Margin = new Thickness(0, 2, 0, 0) };
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentBrush");
            dot.Opacity = isForeground ? 1.0 : 0.4;
            var cell = new StackPanel();
            cell.Children.Add(image);
            cell.Children.Add(dot);
            var slot = new AppSlot(path, icon, active.Handle, first.Title, isForeground, dot) { Windows = entry.Windows };
            Button? button = null;
            button = DockFlyout.FlatButton(cell, (_, _) =>
            {
                if (Application.Current?.MainWindow is MainWindow main
                    && main.TryShowWindowPicker(slot.Windows, button!, slot.Icon, slot.Path, extraGapDips: 0))
                {
                    return;
                }
                NativeWindowTracker.ToggleActivation(slot.TargetHandle, slot.Path);
            }, first.Title);
            button.MouseLeave += (_, _) => (Application.Current?.MainWindow as MainWindow)?.ScheduleWindowPickerClose(button);
            button.Padding = new Thickness(6, 2, 6, 0);
            System.Windows.Automation.AutomationProperties.SetName(button, first.Title);
            _slots.Add(slot);
            _apps.Children.Add(button);
        }
    }

    private bool IsFullScreen(nint window)
        => IsFullScreenOnMonitor(window, _monitor);

    internal static bool IsWindowOnMonitor(nint window, nint monitor)
    {
        if (window == 0) return false;

        var root = GetAncestor(window, 3);
        var targetHandle = root != 0 ? root : window;
        return monitor != 0 && MonitorFromWindow(targetHandle, 2) == monitor;
    }

    internal static bool IsFullScreenOnMonitor(nint window, nint monitor)
    {
        if (!IsWindowOnMonitor(window, monitor)) return false;

        var root = GetAncestor(window, 3);
        var targetHandle = root != 0 ? root : window;

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info) || !GetWindowRect(targetHandle, out var rect))
            return false;

        var monitorRect = new TaskbarReplacementSession.Rect(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom);
        var workRect = new TaskbarReplacementSession.Rect(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom);
        var winRect = new TaskbarReplacementSession.Rect(rect.Left, rect.Top, rect.Right, rect.Bottom);
        var style = GetWindowLongPtr(targetHandle, -16).ToInt64();
        const long wsMaximize = 0x01000000;
        var zoomed = (style & wsMaximize) != 0;

        return TaskbarReplacementSession.IsFullScreenBounds(winRect, style, zoomed, monitorRect, workRect);
    }

    /// <summary>Handles of every display except the primary one.</summary>
    public static List<nint> SecondaryMonitors(string? targetDisplay = null) =>
        DisplayService.GetSecondaryMonitors(targetDisplay).ToList();

    private static uint GetDpiForMonitor(nint monitor) =>
        GetDpiForMonitor(monitor, 0, out var x, out _) == 0 && x > 0 ? x : 96;

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public uint cbSize;
        public nint hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public NativeRect rc;
        public nint lParam;
    }
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    // GetWindowLongPtrW/SetWindowLongPtrW do not exist on 32-bit Windows; there they are macros for GetWindowLongW/SetWindowLongW.
    private static nint GetWindowLongPtr(nint window, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(window, index) : GetWindowLong32(window, index);

    private static nint SetWindowLongPtr(nint window, int index, nint value) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(window, index, value) : SetWindowLong32(window, index, (int)value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr64(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong32(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr64(nint window, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong32(nint window, int index, int value);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("shell32.dll")] private static extern nuint SHAppBarMessage(uint message, ref AppBarData data);
}
