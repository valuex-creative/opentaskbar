using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;
using OpenTaskbar.Models;
using OpenTaskbar.Services;
using OpenTaskbar.Views;
using OpenTaskbar.Widgets;

namespace OpenTaskbar;

public partial class SettingsWindow : Window
{
    private readonly ApplicationPreferences _preferences;
    private readonly StartupRegistration _startup = new();
    private readonly ObservableCollection<DockWidgetOption> _widgetOptions = [];
    private readonly List<ComboBox> _aiRingBoxes = [];
    private readonly Dictionary<AntigravityGroups, CheckBox> _antigravityGroupBoxes = [];
    private readonly List<Action> _aiAccountRefreshers = [];
    private readonly Action<AiProviders> _credentialsChangedHandler;
    private readonly DockWidgetService _widgetPreviewService = new(TimeProvider.System);
    private readonly DispatcherTimer _widgetPreviewTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly List<OpenTaskbar.Widgets.DockWidget> _livePreviewWidgets = [];
    private DispatcherTimer? _livePreviewTimer;
    private bool _livePreviewsAttached;
    private readonly DispatcherTimer _autoSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    // During "reset to defaults", inactive gap-unit values come from the defaults snapshot rather
    // than the last saved settings, so a reset clears both units instead of leaving stale hidden values.
    private DockSettings? _gapBase;
    private bool _startupEnabled;
    private bool _startupAvailable;
    private DockSettings _applied;
    private string? _selectedThemePackId;
    private bool _closingApplication;
    private bool _initializing = true;
    private bool _initializingWeather;
    private bool _isSaving;
    private bool _isInternalSave;
    private bool _hasUnappliedThemePreview;
    private AiUsageRing _aiUsageRing;
    private AntigravityGroups _antigravityGroups = AntigravityGroups.All;
    private bool _closed;
    private bool _sourceReady;
    private HwndSource? _source;
    private enum HotkeyTarget { None, Dock, Launcher }
    private HotkeyTarget _recordingHotkeyTarget;
    private HotkeyChoice _dockHotkeyChoice = HotkeyChoice.CtrlAltD;
    private string? _dockCustomHotkey;
    private HotkeyChoice _launcherHotkeyChoice = HotkeyChoice.AltSpace;
    private string? _launcherCustomHotkey;
    private readonly string _radioGroupScope = Guid.NewGuid().ToString("N");
    private readonly RoutedEventHandler _preferenceEditedHandler;
    private readonly DragCompletedEventHandler _dockScaleDragCompletedHandler;
    private readonly Dictionary<SurfaceKind, Slider> _surfaceSliders = [];
    private readonly Dictionary<SurfaceKind, TextBlock> _surfaceValues = [];
    private readonly Dictionary<SurfaceKind, ComboBox> _surfaceBoxes = [];
    private CancellationTokenSource? _cursorLoginCts;
    private TextBlock? _cursorAccountLabel;
    private TextBlock? _cursorStatusText;
    private Button? _cursorSignInButton;
    private Button? _cursorRefreshButton;
    private Button? _cursorSignOutButton;
    private bool _cursorSignInTimedOut;
    private bool _cursorAuthSubscribed;
    private string? _dynamicIslandBackgroundColor;

    internal bool IsClosingOrClosed => _isClosing || _closed;
    internal int LivePreviewWidgetCount => _livePreviewWidgets.Count;

    public SettingsWindow(ApplicationPreferences preferences)
    {
        _preferences = preferences;
        _applied = preferences.Current;
        _preferenceEditedHandler = PreferenceEdited;
        _dockScaleDragCompletedHandler = (_, _) => CommitDockScale();
        _credentialsChangedHandler = OnAiCredentialsChanged;
        InitializeComponent();
        HardwareAccelerationToggle.IsChecked = !RenderModePreference.IsSoftware();
        HardwareAccelerationToggle.Checked += (_, _) => RenderModePreference.SetSoftware(false);
        HardwareAccelerationToggle.Unchecked += (_, _) => RenderModePreference.SetSoftware(true);
        var currentVersion = UpdateChecker.CurrentVersion;
        var versionString = $"v{currentVersion.Major}.{currentVersion.Minor}.{currentVersion.Build}";
        TitleVersionText.Text = versionString;
        AppVersionText.Text = versionString;
        ScopeRadioGroups(this);
        RegisterSurface(SurfaceKind.Dock, DockOpacitySlider, DockOpacityValue, DockSurfaceBox);
        RegisterSurface(SurfaceKind.Menu, MenuOpacitySlider, MenuOpacityValue, MenuSurfaceBox);
        RegisterSurface(SurfaceKind.Widget, WidgetOpacitySlider, WidgetOpacityValue, WidgetSurfaceBox);
        RegisterSurface(SurfaceKind.Settings, SettingsOpacitySlider, SettingsOpacityValue, SettingsSurfaceBox);
        foreach (var option in _widgetPreviewService.CreateOptions(_applied.InstalledWidgets, _applied.Widgets))
        {
            _widgetOptions.Add(option);
            option.PropertyChanged += WidgetOption_PropertyChanged;
        }
        LoadDynamicIslandPlacementChoices(_applied);
        AttachWidgetSettingsPanels();
        // Live widget previews are attached lazily when WidgetsTab is first selected in UpdateWidgetPreviewRefresh.
        CustomThemeEditor.ThemeSaved += CustomThemeEditor_ThemeSaved;
        CustomThemeEditor.ThemeCreated += CustomThemeEditor_ThemeCreated;
        var widgetView = new ListCollectionView(_widgetOptions);
        widgetView.SortDescriptions.Add(new SortDescription(nameof(DockWidgetOption.CategoryOrder), ListSortDirection.Ascending));
        // Group by the localized label so the catalog headers follow the active UI language.
        widgetView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DockWidgetOption.CategoryLabel)));
        WidgetOptionsList.ItemsSource = widgetView;
        WeatherCountryBox.ItemsSource = CountryCityService.GetInitialCountries().Select(c => c.Name).ToList();
        WeatherCityCombo.LostFocus += WeatherCityCombo_LostFocus;
        _preferences.Changed += Preferences_Changed;
        AiUsageService.CredentialsChanged += _credentialsChangedHandler;
        Deactivated += Settings_Deactivated;
        _widgetPreviewTimer.Tick += WidgetPreviewTimer_Tick;
        _autoSaveTimer.Tick += AutoSaveTimer_Tick;
        IsVisibleChanged += Settings_IsVisibleChanged;
        Closed += (_, _) =>
        {
            _closed = true;
            CancelCursorLogin();
            if (_cursorAuthSubscribed)
            {
                CursorAuthService.SessionChanged -= CursorAuthService_SessionChanged;
                _cursorAuthSubscribed = false;
            }
            _cursorAccountLabel = null;
            _cursorStatusText = null;
            _cursorSignInButton = null;
            _cursorRefreshButton = null;
            _cursorSignOutButton = null;
            ClearSearchHighlight();
            RemoveHandler(ToggleButton.CheckedEvent, _preferenceEditedHandler);
            RemoveHandler(ToggleButton.UncheckedEvent, _preferenceEditedHandler);
            DockScaleSlider.RemoveHandler(Thumb.DragCompletedEvent, _dockScaleDragCompletedHandler);
            CustomThemeEditor.ThemeSaved -= CustomThemeEditor_ThemeSaved;
            CustomThemeEditor.ThemeCreated -= CustomThemeEditor_ThemeCreated;
            WeatherCityCombo.LostFocus -= WeatherCityCombo_LostFocus;
            _autoSaveTimer.Stop();
            _autoSaveTimer.Tick -= AutoSaveTimer_Tick;
            _widgetPreviewTimer.Stop();
            _widgetPreviewTimer.Tick -= WidgetPreviewTimer_Tick;
            StopAndDisposeLivePreviews();
            IsVisibleChanged -= Settings_IsVisibleChanged;
            _preferences.Changed -= Preferences_Changed;
            AiUsageService.CredentialsChanged -= _credentialsChangedHandler;
            Deactivated -= Settings_Deactivated;
            if (_source is { IsDisposed: false }) _source.RemoveHook(WindowProc);
            WidgetOptionsList.ItemsSource = null;
            foreach (var preview in _widgetOptions
                .Where(o => o.NativePreview is OpenTaskbar.Widgets.DockWidget)
                .Select(o => (OpenTaskbar.Widgets.DockWidget)o.NativePreview!))
                preview.Dispose();
            foreach (var option in _widgetOptions)
                option.PropertyChanged -= WidgetOption_PropertyChanged;
            _widgetOptions.Clear();
            _aiRingBoxes.Clear();
            _antigravityGroupBoxes.Clear();
            _aiAccountRefreshers.Clear();
            WeatherCountryBox.ItemsSource = null;
            if (_hasUnappliedThemePreview || !ThemeManager.IsApplied(_preferences.Current))
            {
                ThemeManager.Apply(_preferences.Current.Theme, _preferences.Current);
            }
            _hasUnappliedThemePreview = false;
            (Application.Current.MainWindow as MainWindow)?.SetSizePreview(null, null);
            (Application.Current.MainWindow as MainWindow)?.SetGeometryPreview(null);
        };
        SelectChoice(ThemeBox, _applied.Theme.ToString());
        LanguageBox.ItemsSource = UiText.Languages;
        SelectLanguage(_applied.Language);
        SelectAlignment(_applied);
        AnimationsToggle.IsChecked = _applied.AnimationsEnabled;
        PreviewsToggle.IsChecked = _applied.PreviewsEnabled;
        WindowPickerToggle.IsChecked = _applied.ClickShowsWindowPicker;
        SelectChoice(AutoHideBox, _applied.AutoHide.ToString());
        LoadSurfaceStyles(_applied);
        MagnificationToggle.IsChecked = _applied.MagnificationEnabled;
        SelectChoice(SizeBox, _applied.Size.ToString());
        DockIconPaddingSlider.Value = _applied.DockIconPadding;
        UpdateDockIconPaddingLabel();
        DockScaleSlider.Value = Math.Round(_applied.DockScale * 100);
        DockScaleSlider.AddHandler(Thumb.DragCompletedEvent, _dockScaleDragCompletedHandler);
        UpdateDockScaleLabel();
        SelectChoice(DockWidthModeBox, _applied.DockWidthMode.ToString());
        UpdateDockWidthControls();
        SelectChoice(DockHeightModeBox, _applied.DockHeightMode.ToString());
        UpdateDockHeightControls();
        LoadDockGapControls(_applied);
        var initialProfile = DockMetrics.For(_applied.Size, _applied.DockScale);
        CornerRadiusSlider.Value = _applied.DockCornerRadius ?? (int)Math.Round(initialProfile.Radius);
        UpdateCornerRadiusLabel();
        FocusMinutesSlider.Value = _applied.FocusMinutes;
        BreakMinutesSlider.Value = _applied.FocusBreakMinutes;
        FocusSessionsSlider.Value = _applied.FocusSessions;
        LoadFeatureSelection(_applied);
        LoadThemePacks(_applied.ThemePack);
        UpdateWidgetOptionState();
        var startup = _startup.Read();
        _startupEnabled = startup.Enabled;
        _startupAvailable = startup.Error is null;
        StartupToggle.IsEnabled = _startupAvailable;
        if (startup.Error is not null)
        {
            StartupToggle.ToolTip = UiText.Get(startup.Error);
            System.Windows.Controls.ToolTipService.SetShowOnDisabled(StartupToggle, true);
        }
        StartupToggle.IsChecked = _startupEnabled;
        var errors = new[] { preferences.LoadError, startup.Error }.OfType<string>();
        var message = string.Join(" ", errors.Select(UiText.Get));
        if (message.Length > 0) AppLog.Warning($"Settings initialization warning: {message}");
        var workArea = SystemParameters.WorkArea;
        MinHeight = Math.Min(MinHeight, workArea.Height);
        MinWidth = Math.Min(MinWidth, workArea.Width);
        Height = Math.Min(Height, workArea.Height);
        Width = Math.Min(Width, workArea.Width);
        AddHandler(ToggleButton.CheckedEvent, _preferenceEditedHandler);
        AddHandler(ToggleButton.UncheckedEvent, _preferenceEditedHandler);
        MirrorMainDockToggle.Checked += (_, _) => UpdateMultiMonitorControls();
        MirrorMainDockToggle.Unchecked += (_, _) => UpdateMultiMonitorControls();
        _initializing = false;
    }

    // Segment controls are now ComboBoxes whose items carry the enum name in Tag. These helpers keep the
    // read/write sites terse and avoid sprinkling SelectedItem casts across initialization and save paths.
    private static string? Choice(ComboBox box) =>
        (box.SelectedItem as ComboBoxItem)?.Tag as string;

    private static void SelectChoice(ComboBox box, string? tag)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
            if (string.Equals(item.Tag as string, tag, StringComparison.Ordinal))
            {
                box.SelectedItem = item;
                return;
            }
        box.SelectedIndex = -1;
    }

    // Scope RadioButton.GroupName per window instance so closing one SettingsWindow cannot uncheck
    // another instance's radio buttons via WPF's global group registry.
    private void ScopeRadioGroups(DependencyObject root)
    {
        if (root is Visual)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is RadioButton radio && !string.IsNullOrEmpty(radio.GroupName)
                    && !radio.GroupName.EndsWith(_radioGroupScope, StringComparison.Ordinal))
                {
                    radio.GroupName = $"{radio.GroupName}_{_radioGroupScope}";
                }
                ScopeRadioGroups(child);
            }
        }
        if (root is FrameworkElement fe)
        {
            foreach (var obj in LogicalTreeHelper.GetChildren(fe))
            {
                if (obj is DependencyObject dep && !IsVisualChild(fe, dep))
                {
                    if (dep is RadioButton radio && !string.IsNullOrEmpty(radio.GroupName)
                        && !radio.GroupName.EndsWith(_radioGroupScope, StringComparison.Ordinal))
                    {
                        radio.GroupName = $"{radio.GroupName}_{_radioGroupScope}";
                    }
                    ScopeRadioGroups(dep);
                }
            }
        }

        static bool IsVisualChild(DependencyObject parent, DependencyObject candidate) =>
            candidate is Visual && VisualTreeHelper.GetParent(candidate) == parent;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _source?.AddHook(WindowProc);
        _sourceReady = true;
        UpdateBackdrop();
        if (_preferences.Current.EffectiveAnimationsEnabled)
        {
            PlayOpenAnimation();
        }
    }

    private CornerRadius ActiveCornerRadius => SettingsBackdrop.SupportsNativeRoundedCorners && WindowState != WindowState.Maximized
        ? new CornerRadius(SettingsBackdrop.NativeCornerRadius)
        : new CornerRadius(0);

    private void ApplySettingsBorderCornerRadius()
    {
        if (SettingsBackdrop.SupportsNativeRoundedCorners)
            SettingsBorderOverlay.ClearValue(System.Windows.Controls.Border.CornerRadiusProperty);
        else
            SettingsBorderOverlay.CornerRadius = new CornerRadius(0);
    }

    private void PlayOpenAnimation()
    {
        var fade = new System.Windows.Media.Animation.DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        };
        var scaleAnim = new System.Windows.Media.Animation.DoubleAnimation(0.96, 1.0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        };
        var transform = new ScaleTransform(0.96, 0.96);
        SettingsFrame.CornerRadius = ActiveCornerRadius;
        SettingsFrame.RenderTransformOrigin = new Point(0.5, 0.5);
        SettingsFrame.RenderTransform = transform;
        fade.Completed += (_, _) =>
        {
            if (!_isClosing && !_closed) SettingsFrame.CornerRadius = new CornerRadius(0);
        };
        SettingsFrame.BeginAnimation(UIElement.OpacityProperty, fade);
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
    }

    private nint WindowProc(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != 0x0024 || lParam == 0) return 0; // WM_GETMINMAXINFO
        var monitor = MonitorFromWindow(window, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info))
        {
            AppLog.Warning($"Settings monitor work area unavailable: {Marshal.GetLastWin32Error()}");
            return 0;
        }
        var sizing = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        sizing.MaxPosition = new NativePoint(info.Work.Left - info.Monitor.Left, info.Work.Top - info.Monitor.Top);
        sizing.MaxSize = new NativePoint(info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
        var dpi = GetDpiForWindow(window);
        var scale = dpi > 0 ? dpi / 96d : 1;
        sizing.MinTrackSize = new NativePoint((int)Math.Ceiling(MinWidth * scale), (int)Math.Ceiling(MinHeight * scale));
        if (MaxWidth > 0 && !double.IsInfinity(MaxWidth) && !double.IsNaN(MaxWidth))
            sizing.MaxTrackSize = new NativePoint((int)Math.Ceiling(MaxWidth * scale), (int)Math.Ceiling(MaxHeight * scale));
        Marshal.StructureToPtr(sizing, lParam, false);
        handled = true;
        return 0;
    }

    private void Preferences_Changed(object? sender, EventArgs e)
    {
        // A save we initiated itself raises Changed; ignore it so the debounced auto-save
        // does not re-save the same values and re-enter this handler.
        if (_isSaving) return;

        // Reflect external Focus Mode changes (e.g. the dock context-menu toggle) without
        // rewriting the saved settings or the user's widget/animation choices. Suppress the
        // toggle's Checked/Unchecked event so this sync does not raise a second save.
        if (FocusModeToggle.IsChecked != _preferences.Current.FocusModeEnabled)
        {
            var wasInitializing = _initializing;
            _initializing = true;
            FocusModeToggle.IsChecked = _preferences.Current.FocusModeEnabled;
            _initializing = wasInitializing;
        }
        if (_applied.Language != _preferences.Current.Language)
        {
            var localizedOptions = _widgetPreviewService.CreateOptions([], []).ToDictionary(option => option.Id, StringComparer.Ordinal);
            foreach (var option in _widgetOptions)
            {
                var localized = localizedOptions[option.Id];
                option.UpdateText(localized.Label, localized.Description);
                option.RefreshInstallLabels();
            }
            RefreshWidgetPreviews();
            foreach (var refresh in _aiAccountRefreshers) refresh();
            UpdateHotkeyControls();
            UpdateCursorAuthUi();
            RefreshSettingsSearchIfActive();
        }
        var themeGalleryDirty = _applied.Theme != _preferences.Current.Theme
            || !string.Equals(_applied.ThemePack, _preferences.Current.ThemePack, StringComparison.OrdinalIgnoreCase)
            || _applied.Language != _preferences.Current.Language;
        var reloadedSelections = false;
        if (!_isInternalSave && !_isSaving && !ReferenceEquals(_preferences.Current, _applied))
        {
            if (_autoSaveTimer.IsEnabled)
            {
                _autoSaveTimer.Stop();
                SaveSettings();
            }
            else
            {
                var wasInitializing = _initializing;
                _initializing = true;
                try
                {
                    LoadAllSelections(_preferences.Current);
                    reloadedSelections = true;
                }
                finally
                {
                    _initializing = wasInitializing;
                }
            }
        }
        var themeApplied = false;
        if (_hasUnappliedThemePreview || !ThemeManager.IsApplied(_preferences.Current))
        {
            ThemeManager.Apply(_preferences.Current.Theme, _preferences.Current);
            themeApplied = true;
        }
        _hasUnappliedThemePreview = false;
        if (!_isInternalSave && !reloadedSelections)
        {
            LoadSurfaceStyles(_preferences.Current);
        }
        ApplySettingsToLivePreviews(_preferences.Current);
        foreach (var option in _widgetOptions
            .Where(o => o.NativePreview is OpenTaskbar.Widgets.DockWidget d
                        && !_livePreviewWidgets.Contains(d)))
        {
            var native = (OpenTaskbar.Widgets.DockWidget)option.NativePreview!;
            native.ApplySettings(_preferences.Current);
            native.SetCompactState(native.SupportsCompactMode && _preferences.Current.GlobalWidgetDensity == WidgetDensityMode.Compact);
            native.Visibility = Visibility.Visible;
        }
        if ((themeGalleryDirty || themeApplied) && !reloadedSelections)
        {
            CustomThemeEditor.LoadTheme(_selectedThemePackId, ThemeManager.EffectiveTheme);
            BuildThemeGallery(_selectedThemePackId);
        }
        UpdateDockScaleLabel();
        UpdateWidgetOptionState();
        UpdateBackdrop();
        _applied = _preferences.Current;
    }

    private void UpdateBackdrop()
    {
        if (!_sourceReady) return;
        var chrome = WindowChrome.GetWindowChrome(this);
        var surfaceMode = ThemeManager.GetSurface(SurfaceKind.Settings).Background;
        SettingsFrame.CornerRadius = new CornerRadius(0);
        ApplySettingsBorderCornerRadius();
        if (surfaceMode is SurfaceBackground.None or SurfaceBackground.Solid)
        {
            SettingsBackdrop.Disable(this, ThemeManager.EffectiveTheme);
            SetResourceReference(BackgroundProperty, "SettingsBackground");
            SettingsFrame.SetResourceReference(
                System.Windows.Controls.Border.BackgroundProperty,
                surfaceMode == SurfaceBackground.Solid ? "SettingsGlassMaterial" : "SettingsBackground");
            chrome.GlassFrameThickness = new Thickness(0);
            return;
        }

        // Glass mode uses Windows 11 native Desktop Acrylic (DWMSBT_TRANSIENTWINDOW) while WPF's
        // SettingsGlassMaterial applies the active theme hue and the user's opacity slider.
        // Set GlassFrameThickness before enabling acrylic so WindowChrome does not overwrite the DWM frame.
        chrome.GlassFrameThickness = new Thickness(-1);
        var glass = SettingsBackdrop.TryEnable(this, ThemeManager.EffectiveTheme);
        if (glass)
        {
            Background = Brushes.Transparent;
            SettingsFrame.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "SettingsGlassMaterial");
        }
        else
        {
            SettingsBackdrop.Disable(this, ThemeManager.EffectiveTheme);
            SetResourceReference(BackgroundProperty, "SettingsBackground");
            SettingsFrame.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "SettingsBackground");
            chrome.GlassFrameThickness = new Thickness(0);
        }
    }

    private DockSettings ReadSelection()
    {
        var enabledWidgets = _widgetOptions.Where(option => option.IsInstalled && option.IsEnabled).Select(option => option.Id).ToArray();
        var installedWidgets = _widgetOptions.Where(option => option.IsInstalled).Select(option => option.Id).ToArray();
        var islandPlacements = new Dictionary<string, string>(
            _preferences.Current.DynamicIslandPlacements ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var option in _widgetOptions)
        {
            if (string.Equals(option.DynamicIslandPlacementValue, "dock", StringComparison.OrdinalIgnoreCase))
                islandPlacements.Remove(option.Id);
            else
                islandPlacements[option.Id] = option.DynamicIslandPlacementValue;
        }
        bool IsEnabled(string id) => enabledWidgets.Contains(id, StringComparer.OrdinalIgnoreCase);
        return _preferences.Current with
        {
            Theme = Enum.TryParse<AppTheme>(Choice(ThemeBox), out var theme) ? theme : AppTheme.Dark,
            Language = LanguageBox.SelectedItem is AppLanguage language ? language : _preferences.Current.Language,
            // Full width keeps the last floating alignment so switching back restores it.
            Position = Choice(PositionBox) is { } alignment && alignment != "FullWidth" && Enum.TryParse<DockPosition>(alignment, out var position)
                ? position : _preferences.Current.Position,
            ContentAlignment = Enum.TryParse<DockContentAlignment>(Choice(ContentAlignmentBox), out var contentAlignment)
                ? contentAlignment : DockContentAlignment.Center,
            AnimationsEnabled = AnimationsToggle.IsChecked == true,
            PreviewsEnabled = PreviewsToggle.IsChecked == true,
            ClickShowsWindowPicker = WindowPickerToggle.IsChecked == true,
            NotificationBadgesEnabled = _preferences.Current.NotificationBadgesEnabled,
            FocusModeEnabled = FocusModeToggle.IsChecked == true,
            AutoHide = Enum.TryParse<AutoHideMode>(Choice(AutoHideBox), out var autoHide) ? autoHide : AutoHideMode.Disabled,
            GlobalWidgetDensity = Enum.TryParse<WidgetDensityMode>(Choice(DensityBox), out var density) ? density : WidgetDensityMode.Auto,
            // Density is global now; clear any overrides written by older versions.
            WidgetDensityOverrides = null,
            DockBackground = SurfaceBackgroundOf(SurfaceKind.Dock),
            DockOpacity = Math.Round(_surfaceSliders[SurfaceKind.Dock].Value) / 100,
            DockBorderThickness = BorderThicknessSlider.Value,
            DockBorderColor = _borderColor,
            MenuBackground = SurfaceBackgroundOf(SurfaceKind.Menu),
            MenuOpacity = Math.Round(_surfaceSliders[SurfaceKind.Menu].Value) / 100,
            MenuBorderThickness = BorderThicknessSlider.Value,
            MenuBorderColor = _borderColor,
            WidgetBackground = SurfaceBackgroundOf(SurfaceKind.Widget),
            WidgetOpacity = Math.Round(_surfaceSliders[SurfaceKind.Widget].Value) / 100,
            WidgetBorderThickness = BorderThicknessSlider.Value,
            WidgetBorderColor = _borderColor,
            SettingsBackground = SurfaceBackgroundOf(SurfaceKind.Settings),
            SettingsOpacity = Math.Round(_surfaceSliders[SurfaceKind.Settings].Value) / 100,
            SettingsBorderThickness = BorderThicknessSlider.Value,
            SettingsBorderColor = _borderColor,
            Widgets = enabledWidgets,
            InstalledWidgets = installedWidgets,
            DynamicIslandEnabled = DynamicIslandEnabledToggle.IsChecked == true,
            DynamicIslandPlacements = islandPlacements.Count == 0 ? null : islandPlacements,
            DynamicIslandVisibility = Enum.TryParse<DynamicIslandVisibilityMode>(Choice(DynamicIslandVisibilityBox), out var islandVisibility)
                ? islandVisibility : _preferences.Current.DynamicIslandVisibility,
            DynamicIslandHideInFullscreen = DynamicIslandHideFullscreenToggle.IsChecked == true,
            DynamicIslandAutoHide = DynamicIslandAutoHideToggle.IsChecked == true,
            DynamicIslandBackgroundColor = _dynamicIslandBackgroundColor,
            DynamicIslandOpacity = Math.Round(DynamicIslandOpacitySlider.Value) / 100,
            MagnificationEnabled = MagnificationToggle.IsChecked == true,
            Layout = Choice(PositionBox) == "FullWidth" ? DockLayout.FullWidth : DockLayout.Floating,
            Size = Enum.TryParse<DockSize>(Choice(SizeBox), out var size) ? size : DockSize.Compact,
            DockIconPadding = (int)Math.Round(DockIconPaddingSlider.Value),
            DockScale = Math.Round(DockScaleSlider.Value) / 100,
            DockWidthMode = Enum.TryParse<DockWidthMode>(Choice(DockWidthModeBox), out var widthMode) ? widthMode : DockWidthMode.Auto,
            DockWidthPixels = widthMode == DockWidthMode.Pixels ? (int)Math.Round(DockWidthSlider.Value) : _preferences.Current.DockWidthPixels,
            DockWidthPercent = widthMode == DockWidthMode.Percent ? (int)Math.Round(DockWidthSlider.Value) : _preferences.Current.DockWidthPercent,
            DockHeightMode = Enum.TryParse<DockHeightMode>(Choice(DockHeightModeBox), out var heightMode) ? heightMode : DockHeightMode.Auto,
            DockHeightPixels = heightMode == DockHeightMode.Pixels ? (int)Math.Round(DockHeightSlider.Value) : _preferences.Current.DockHeightPixels,
            DockHeightPercent = heightMode == DockHeightMode.Percent ? (int)Math.Round(DockHeightSlider.Value) : _preferences.Current.DockHeightPercent,
            DockTopGapUnit = GapUnit(DockTopGapUnitBox) is var topGapUnit ? topGapUnit : DockGapUnit.Pixels,
            DockTopGapPixels = topGapUnit == DockGapUnit.Pixels
                ? Math.Clamp((int)Math.Round(DockTopGapSlider.Value), 0, 200)
                : Math.Clamp((_gapBase ?? _preferences.Current).DockTopGapPixels, 0, 200),
            DockTopGapPercent = topGapUnit == DockGapUnit.Percent
                ? Math.Clamp((int)Math.Round(DockTopGapSlider.Value), 0, 100)
                : Math.Clamp((_gapBase ?? _preferences.Current).DockTopGapPercent, 0, 100),
            DockBottomGapUnit = GapUnit(DockBottomGapUnitBox) is var bottomGapUnit ? bottomGapUnit : DockGapUnit.Pixels,
            DockBottomGapPixels = bottomGapUnit == DockGapUnit.Pixels
                ? Math.Clamp((int)Math.Round(DockBottomGapSlider.Value), 0, 200)
                : Math.Clamp((_gapBase ?? _preferences.Current).DockBottomGapPixels, 0, 200),
            DockBottomGapPercent = bottomGapUnit == DockGapUnit.Percent
                ? Math.Clamp((int)Math.Round(DockBottomGapSlider.Value), 0, 100)
                : Math.Clamp((_gapBase ?? _preferences.Current).DockBottomGapPercent, 0, 100),
            DockCornerRadius = (int)Math.Round(CornerRadiusSlider.Value),
            ShowClock = IsEnabled("Clock"),
            ShowMedia = IsEnabled("Media"),
            ShowSystemStats = IsEnabled("SystemStats"),
            ShowAudio = IsEnabled("Audio"),
            ShowQuickSettings = IsEnabled("QuickSettings"),
            ShowFocus = IsEnabled("Focus"),
            FocusMinutes = (int)Math.Round(FocusMinutesSlider.Value),
            FocusBreakMinutes = (int)Math.Round(BreakMinutesSlider.Value),
            FocusSessions = (int)Math.Round(FocusSessionsSlider.Value),
            ShowRecycleBin = RecycleBinToggle.IsChecked == true,
            ShowSearchButton = SearchButtonToggle.IsChecked == true,
            ShowTaskViewButton = TaskViewButtonToggle.IsChecked == true,
            ShowTrayButton = TrayButtonToggle.IsChecked == true,
            ShowDesktopButton = ShowDesktopButtonToggle.IsChecked == true,
            TargetDisplay = (TargetDisplayBox.SelectedItem as DisplayChoice)?.DeviceName,
            ShowOnAllMonitors = AllMonitorsToggle.IsChecked == true,
            SecondaryApps = (SecondaryAppsBox.SelectedItem as SecondaryAppChoice)?.Scope ?? SecondaryAppScope.CurrentMonitorOnly,
            SecondaryWidgets = SecondaryWidgetsToggle.IsChecked == true,
            MirrorMainDock = MirrorMainDockToggle.IsChecked == true,
            RevealDockOnWinKey = RevealOnWinKeyToggle.IsChecked == true,
            CurrentDesktopOnly = CurrentDesktopToggle.IsChecked == true,
            CheckUpdates = UpdatesToggle.IsChecked == true,
            Edge = Enum.TryParse<DockEdge>(Choice(EdgeBox), out var edge) ? edge : DockEdge.Bottom,
            ShowWeather = IsEnabled("Weather"),
            WeatherCity = WeatherCityBox.Text.Trim(),
            LauncherHotkey = _launcherHotkeyChoice,
            CustomLauncherHotkey = _launcherCustomHotkey,
            DockHotkey = _dockHotkeyChoice,
            CustomDockHotkey = _dockCustomHotkey,
            StartAction = StartButtonAction.WindowsStart,
            StartPlacement = Enum.TryParse<StartButtonPlacement>(Choice(StartPlacementBox), out var placement)
                ? placement : _preferences.Current.StartPlacement,
            StartWidgetIndex = _preferences.Current.StartWidgetIndex,
            WheelVolumeEnabled = WheelVolumeToggle.IsChecked == true,
            Clock24Hour = Choice(ClockFormatBox) != "12",
            ClockShowSeconds = ClockSecondsToggle.IsChecked == true,
            DateStyle = Enum.TryParse<ClockDateStyle>(Choice(DateStyleBox), out var dateStyle) ? dateStyle : ClockDateStyle.DayAndShortDate,
            DateFormat = Enum.TryParse<ClockDateFormat>(Choice(DateFormatBox), out var dateFormat) ? dateFormat : ClockDateFormat.System,
            ClockDateLayout = Enum.TryParse<ClockDateLayout>(Choice(ClockDateLayoutBox), out var clockDateLayout) ? clockDateLayout : ClockDateLayout.Beside,
            WeatherUnit = Enum.TryParse<TemperatureUnit>(Choice(WeatherUnitBox), out var weatherUnit) ? weatherUnit : TemperatureUnit.Celsius,
            StatsMeters = ReadStatsMeters(),
            AiUsageRing = _aiUsageRing,
            AntigravityGroups = _antigravityGroups,
            ThemePack = _selectedThemePackId,
            ThemeApplicationIcons = false
        };
    }

    private sealed record ThemePackChoice(string? Id, string Name, string Details, ThemePack? Pack = null);
    private sealed record DisplayChoice(string? DeviceName, string DisplayName);
    private sealed record SecondaryAppChoice(SecondaryAppScope Scope, string DisplayName);

    private void LoadDisplayChoices(string? selectedDevice)
    {
        var primaryLabel = UiText.Get("TargetDisplayPrimary");
        var choices = new List<DisplayChoice> { new(null, primaryLabel) };
        try
        {
            var displays = DisplayService.GetDisplays();
            foreach (var d in displays)
            {
                var suffix = d.IsPrimary ? $" ({primaryLabel})" : "";
                var label = $"{d.Index}: {d.Bounds.Width:0}x{d.Bounds.Height:0}{suffix}";
                choices.Add(new DisplayChoice(d.DeviceName, label));
            }
        }
        catch (Exception ex)
        {
            AppLog.ReportError("ErrorDisplayDimensions", ex);
        }

        TargetDisplayBox.ItemsSource = choices;
        TargetDisplayBox.SelectedItem = !string.IsNullOrWhiteSpace(selectedDevice)
            ? choices.FirstOrDefault(c => string.Equals(c.DeviceName, selectedDevice, StringComparison.OrdinalIgnoreCase)) ?? choices[0]
            : choices[0];
    }

    private void LoadSecondaryAppChoices(SecondaryAppScope selectedScope)
    {
        var choices = new List<SecondaryAppChoice>
        {
            new(SecondaryAppScope.CurrentMonitorOnly, UiText.Get("SecondaryAppsCurrent")),
            new(SecondaryAppScope.AllMonitors, UiText.Get("SecondaryAppsAll"))
        };
        SecondaryAppsBox.ItemsSource = choices;
        SecondaryAppsBox.SelectedItem = choices.FirstOrDefault(c => c.Scope == selectedScope) ?? choices[0];
    }

    private void UpdateMultiMonitorControls()
    {
        var allMonitors = AllMonitorsToggle.IsChecked == true;
        MirrorMainDockRow.IsEnabled = allMonitors;
        SecondaryAppsRow.IsEnabled = allMonitors;
        SecondaryWidgetsRow.IsEnabled = allMonitors && MirrorMainDockToggle.IsChecked != true;
    }

    private void DynamicIslandEnabled_Changed(object sender, RoutedEventArgs e) => UpdateDynamicIslandControls();

    private void UpdateDynamicIslandControls()
    {
        var enabled = DynamicIslandEnabledToggle.IsChecked == true;
        DynamicIslandVisibilityBox.IsEnabled = enabled;
        DynamicIslandHideFullscreenToggle.IsEnabled = enabled;
        DynamicIslandAutoHideToggle.IsEnabled = enabled;
        DynamicIslandBackgroundButton.IsEnabled = enabled;
        DynamicIslandUseThemeButton.IsEnabled = enabled;
        DynamicIslandOpacitySlider.IsEnabled = enabled;
    }

    private void LoadDynamicIslandAppearance(DockSettings settings)
    {
        _dynamicIslandBackgroundColor = settings.DynamicIslandBackgroundColor;
        DynamicIslandOpacitySlider.Value = Math.Clamp(settings.DynamicIslandOpacity * 100, 0, 100);
        DynamicIslandOpacityValue.Text = UiText.Format("OpacityPercent", (int)Math.Round(DynamicIslandOpacitySlider.Value));
        UpdateDynamicIslandBackgroundSwatch();
    }

    private void UpdateDynamicIslandBackgroundSwatch()
    {
        if (_dynamicIslandBackgroundColor is not null
            && ThemeManager.TryParseColor(_dynamicIslandBackgroundColor, out var color))
        {
            DynamicIslandBackgroundSwatch.Background = new SolidColorBrush(color);
            return;
        }

        DynamicIslandBackgroundSwatch.SetResourceReference(Border.BackgroundProperty, "DynamicIslandMaterial");
    }

    private void DynamicIslandOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DynamicIslandOpacityValue is null) return;
        DynamicIslandOpacityValue.Text = UiText.Format("OpacityPercent", (int)Math.Round(e.NewValue));
        if (_initializing) return;
        _hasUnappliedThemePreview = true;
        ThemeManager.SetDynamicIslandStyle(_dynamicIslandBackgroundColor, Math.Round(e.NewValue) / 100);
        PreferenceEdited(sender, e);
    }

    private void DynamicIslandBackgroundButton_Click(object sender, RoutedEventArgs e)
    {
        var source = ThemeManager.GetDynamicIslandBackgroundSource();
        var picker = new ThemeBrushPicker(source) { Owner = this };
        if (picker.ShowDialog() != true || ThemeBrushPicker.BrushToHex(picker.Result) is not { } color) return;

        _dynamicIslandBackgroundColor = color;
        UpdateDynamicIslandBackgroundSwatch();
        _hasUnappliedThemePreview = true;
        ThemeManager.SetDynamicIslandStyle(_dynamicIslandBackgroundColor, DynamicIslandOpacitySlider.Value / 100);
        PreferenceEdited(sender, e);
    }

    private void DynamicIslandUseTheme_Click(object sender, RoutedEventArgs e)
    {
        _dynamicIslandBackgroundColor = null;
        UpdateDynamicIslandBackgroundSwatch();
        _hasUnappliedThemePreview = true;
        ThemeManager.SetDynamicIslandStyle(null, DynamicIslandOpacitySlider.Value / 100);
        PreferenceEdited(sender, e);
    }

    private void LoadDynamicIslandPlacementChoices(DockSettings settings)
    {
        IReadOnlyList<DynamicIslandPlacementChoice> choices =
        [
            new("dock", UiText.Get("DynamicIslandDock")),
            new(DockSettings.DynamicIslandAllDisplays, UiText.Get("DynamicIslandTitle"))
        ];

        foreach (var option in _widgetOptions)
        {
            option.DynamicIslandPlacementChoices = choices;
            option.DynamicIslandPlacementValue = settings.DynamicIslandPlacements?.ContainsKey(option.Id) == true
                ? DockSettings.DynamicIslandAllDisplays
                : "dock";
        }
    }

    private void WidgetOption_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DockWidgetOption.DynamicIslandPlacementValue) || _initializing || _closed) return;
        if (sender is DockWidgetOption option
            && !string.Equals(option.DynamicIslandPlacementValue, "dock", StringComparison.OrdinalIgnoreCase))
        {
            if (option.IsHardwareAvailable && (!option.IsInstalled || !option.IsEnabled))
            {
                var prev = _initializing;
                _initializing = true;
                option.IsInstalled = true;
                option.IsEnabled = true;
                _initializing = prev;
                UpdateWidgetOptionState();
            }
            if (DynamicIslandEnabledToggle.IsChecked != true)
            {
                var prev = _initializing;
                _initializing = true;
                DynamicIslandEnabledToggle.IsChecked = true;
                _initializing = prev;
                UpdateDynamicIslandControls();
            }
        }
        TriggerAutoSave(immediate: true);
    }

    private void UpdateAutoHideControls()
    {
        var autoHideEnabled = !string.Equals(Choice(AutoHideBox), "Disabled", StringComparison.OrdinalIgnoreCase);
        RevealOnWinKeyRow.IsEnabled = autoHideEnabled;
    }

    private void TargetDisplayBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        PreferenceEdited(sender, e);
    }

    private void SecondaryAppsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        PreferenceEdited(sender, e);
    }

    private void SelectLanguage(AppLanguage language)
    {
        if (LanguageBox.ItemsSource is IEnumerable<AppLanguage> languages)
        {
            var match = languages.FirstOrDefault(cand => string.Equals(cand.CultureName, language.CultureName, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(match.CultureName) || match == AppLanguage.English)
            {
                LanguageBox.SelectedItem = match;
            }
        }
        if (LanguageBox.SelectedIndex < 0 && LanguageBox.Items.Count > 0) LanguageBox.SelectedIndex = 0;
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => PreferenceEdited(sender, e);

    private void LoadThemePacks(string? selected)
    {
        _selectedThemePackId = string.IsNullOrWhiteSpace(selected) ? null : selected;
        BuildThemeGallery(_selectedThemePackId);
        CustomThemeEditor.LoadTheme(_selectedThemePackId, ThemeManager.EffectiveTheme);
    }

    private void BuildThemeGallery(string? selected)
    {
        var discovered = ThemePacks.Discover();
        var builtIn = new List<ThemePackChoice> { new(null, UiText.Get("ThemePackNone"), string.Empty) };
        var custom = new List<ThemePackChoice>();

        foreach (var pack in discovered)
        {
            var choice = new ThemePackChoice(pack.Id, pack.DisplayName, DescribeThemePack(pack), pack);
            if (ThemePack.IsBuiltInTheme(pack.Id)) builtIn.Add(choice);
            else custom.Add(choice);
        }

        // A stored theme whose folder is gone stays listed so an unrelated edit does not silently clear it.
        if (!string.IsNullOrWhiteSpace(selected)
            && !builtIn.Any(c => string.Equals(c.Id, selected, StringComparison.OrdinalIgnoreCase))
            && !custom.Any(c => string.Equals(c.Id, selected, StringComparison.OrdinalIgnoreCase)))
        {
            custom.Add(new ThemePackChoice(selected, selected + " (?)", UiText.Get("ThemePackMissing")));
        }

        ThemeGalleryBuiltIn.Children.Clear();
        foreach (var choice in builtIn)
            ThemeGalleryBuiltIn.Children.Add(CreateThemeEntryButton(choice, string.Equals(choice.Id, selected, StringComparison.OrdinalIgnoreCase)));

        ThemeGalleryCustom.Children.Clear();
        foreach (var choice in custom)
            ThemeGalleryCustom.Children.Add(CreateThemeEntryButton(choice, string.Equals(choice.Id, selected, StringComparison.OrdinalIgnoreCase)));

        ThemeGalleryCustomEmpty.Visibility = custom.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var activeChoice = builtIn.Concat(custom).FirstOrDefault(c => string.Equals(c.Id, selected, StringComparison.OrdinalIgnoreCase)) ?? builtIn[0];
        ThemePackDetails.Text = activeChoice.Details;
        ThemePackDetails.Visibility = activeChoice.Details.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        var hasSelectedPack = !string.IsNullOrWhiteSpace(selected);
        ThemePackDuplicateButton.IsEnabled = hasSelectedPack;
        ThemePackExportButton.IsEnabled = hasSelectedPack;
        ThemePackDeleteButton.IsEnabled = hasSelectedPack && !ThemePack.IsBuiltInTheme(selected);
    }

    private Button CreateThemeEntryButton(ThemePackChoice choice, bool isActive)
    {
        var (bgColor, dockColor, accentColor, textColor) = ResolveThemePreviewColors(choice.Pack);

        // Miniature desktop backdrop with a floating dock pill previewing the theme's real dock surface and accent.
        var backdropBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops = new GradientStopCollection
            {
                new GradientStop(BlendColors(bgColor, accentColor, 0.14), 0),
                new GradientStop(bgColor, 0.65),
                new GradientStop(BlendColors(bgColor, Colors.Black, ThemeManager.EffectiveTheme == AppTheme.Dark ? 0.25 : 0.06), 1)
            }
        };

        var previewStage = new Border
        {
            Height = 76,
            Background = backdropBrush,
            CornerRadius = new CornerRadius(8, 8, 0, 0),
            ClipToBounds = true
        };

        // Subtle miniature app tiles + active accent dot + mini clock inside a floating dock pill.
        var dockItems = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        // Start / accent tile
        dockItems.Children.Add(new Border
        {
            Width = 10,
            Height = 10,
            CornerRadius = new CornerRadius(3),
            Margin = new Thickness(0, 0, 5, 0),
            Background = new SolidColorBrush(Color.FromArgb(0xCC, accentColor.R, accentColor.G, accentColor.B))
        });

        // Active app tile with accent indicator dot underneath
        var activeAppStack = new StackPanel
        {
            Margin = new Thickness(0, 1, 5, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        activeAppStack.Children.Add(new Border
        {
            Width = 10,
            Height = 10,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(Color.FromArgb(0x3D, textColor.R, textColor.G, textColor.B))
        });
        activeAppStack.Children.Add(new Border
        {
            Width = 3,
            Height = 2,
            CornerRadius = new CornerRadius(1),
            Margin = new Thickness(0, 1.5, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = new SolidColorBrush(accentColor)
        });
        dockItems.Children.Add(activeAppStack);

        // Two resting app tiles
        for (var i = 0; i < 2; i++)
        {
            dockItems.Children.Add(new Border
            {
                Width = 10,
                Height = 10,
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(0, 0, 5, 0),
                Background = new SolidColorBrush(Color.FromArgb(0x28, textColor.R, textColor.G, textColor.B))
            });
        }

        // Divider + mini clock widget placeholder
        dockItems.Children.Add(new Border
        {
            Width = 1,
            Height = 11,
            Margin = new Thickness(1, 0, 6, 0),
            Background = new SolidColorBrush(Color.FromArgb(0x24, textColor.R, textColor.G, textColor.B))
        });
        var miniClock = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        miniClock.Children.Add(new Border
        {
            Width = 18,
            Height = 3.5,
            CornerRadius = new CornerRadius(1.5),
            Background = new SolidColorBrush(Color.FromArgb(0x99, textColor.R, textColor.G, textColor.B))
        });
        miniClock.Children.Add(new Border
        {
            Width = 12,
            Height = 2.5,
            CornerRadius = new CornerRadius(1),
            Margin = new Thickness(0, 2, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Color.FromArgb(0x4D, textColor.R, textColor.G, textColor.B))
        });
        dockItems.Children.Add(miniClock);

        var dockPill = new Border
        {
            Height = 26,
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(8, 0, 8, 0),
            Margin = new Thickness(10, 0, 10, 10),
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(0xEB, dockColor.R, dockColor.G, dockColor.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x44, accentColor.R, accentColor.G, accentColor.B)),
            BorderThickness = new Thickness(1),
            Child = dockItems
        };
        previewStage.Child = dockPill;

        // Footer: left-aligned theme name + right-aligned active checkmark badge or palette dots.
        var footer = new Grid { Margin = new Thickness(10, 8, 10, 9) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nameText = new TextBlock
        {
            Text = choice.Name,
            FontSize = 12,
            FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Medium,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = choice.Name
        };
        nameText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        Grid.SetColumn(nameText, 0);
        footer.Children.Add(nameText);

        if (isActive)
        {
            var checkIcon = new ContentControl
            {
                Width = 10,
                Height = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Content = Application.Current.TryFindResource("IconCheck")
            };
            checkIcon.SetResourceReference(StyleProperty, "WidgetIcon");
            checkIcon.SetResourceReference(Control.ForegroundProperty, "AccentText");

            var badge = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = checkIcon
            };
            badge.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            Grid.SetColumn(badge, 1);
            footer.Children.Add(badge);
        }
        else
        {
            var dots = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            dots.Children.Add(new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(0, 0, 3, 0),
                Background = new SolidColorBrush(dockColor),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, textColor.R, textColor.G, textColor.B)),
                BorderThickness = new Thickness(1)
            });
            dots.Children.Add(new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(accentColor)
            });
            Grid.SetColumn(dots, 1);
            footer.Children.Add(dots);
        }

        var divider = new Border { Height = 1 };
        divider.SetResourceReference(Border.BackgroundProperty, "StrokeSubtle");

        var cardBody = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        cardBody.Children.Add(previewStage);
        cardBody.Children.Add(divider);
        cardBody.Children.Add(footer);

        var cardBorder = new Border
        {
            Child = cardBody,
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(isActive ? 1.5 : 1),
            ClipToBounds = true,
            SnapsToDevicePixels = true
        };
        cardBorder.SetResourceReference(Border.BackgroundProperty, "SettingsControl");
        cardBorder.SetResourceReference(Border.BorderBrushProperty, isActive ? "AccentBrush" : "StrokeSubtle");

        var btn = new Button
        {
            Content = cardBorder,
            Padding = new Thickness(0),
            Margin = new Thickness(5, 0, 5, 10),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top,
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            FocusVisualStyle = null
        };
        btn.Template = CreateThemeCardButtonTemplate();
        System.Windows.Automation.AutomationProperties.SetName(btn, choice.Name);
        btn.Click += (_, e) =>
        {
            if (string.Equals(_selectedThemePackId, choice.Id, StringComparison.OrdinalIgnoreCase)) return;
            _selectedThemePackId = choice.Id;
            ThemePacks.Select(_selectedThemePackId, reload: true);
            ThemePacks.ApplyWidgets(_preferences.Current.Theme);
            BuildThemeGallery(_selectedThemePackId);
            CustomThemeEditor.LoadTheme(_selectedThemePackId, ThemeManager.EffectiveTheme);
            PreferenceEdited(btn, e);
        };
        return btn;
    }

    private static ControlTemplate CreateThemeCardButtonTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var grid = new FrameworkElementFactory(typeof(Grid));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Stretch);

        var hoverOverlay = new FrameworkElementFactory(typeof(Border));
        hoverOverlay.Name = "HoverOverlay";
        hoverOverlay.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
        hoverOverlay.SetResourceReference(Border.BackgroundProperty, "SurfaceHover");
        hoverOverlay.SetValue(UIElement.OpacityProperty, 0.0);
        hoverOverlay.SetValue(UIElement.IsHitTestVisibleProperty, false);

        grid.AppendChild(presenter);
        grid.AppendChild(hoverOverlay);
        template.VisualTree = grid;

        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(UIElement.OpacityProperty, 1.0, "HoverOverlay"));
        template.Triggers.Add(hoverTrigger);
        return template;
    }

    private static Color BlendColors(Color baseColor, Color tint, double amount)
    {
        var t = Math.Clamp(amount, 0, 1);
        var inv = 1 - t;
        return Color.FromRgb(
            (byte)Math.Round(baseColor.R * inv + tint.R * t),
            (byte)Math.Round(baseColor.G * inv + tint.G * t),
            (byte)Math.Round(baseColor.B * inv + tint.B * t));
    }

    private static (Color Bg, Color Dock, Color Accent, Color Text) ResolveThemePreviewColors(ThemePack? pack)
    {
        var dark = ThemeManager.EffectiveTheme == AppTheme.Dark;
        var defaultBg = dark ? Color.FromRgb(0x1B, 0x20, 0x29) : Color.FromRgb(0xEB, 0xEE, 0xF3);
        var defaultDock = dark ? Color.FromRgb(0x25, 0x2B, 0x37) : Color.FromRgb(0xFF, 0xFF, 0xFF);
        var defaultAccent = dark ? Color.FromRgb(0x38, 0xBD, 0xF8) : Color.FromRgb(0x03, 0x69, 0xA1);
        var defaultText = dark ? Color.FromRgb(0xF3, 0xF4, 0xF6) : Color.FromRgb(0x1B, 0x1B, 0x1B);
        if (pack?.Widgets?.Manifest is not { } manifest) return (defaultBg, defaultDock, defaultAccent, defaultText);

        var map = dark ? manifest.Dark : manifest.Light;
        var bg = TryReadColor(map, manifest.Tokens, "SettingsBackground", defaultBg);
        var dock = TryReadColor(map, manifest.Tokens, "DockMaterial", bg);
        var accent = TryReadColor(map, manifest.Tokens, "AccentBrush",
            TryReadColor(map, manifest.Tokens, "WidgetAccent", defaultAccent));
        var text = TryReadColor(map, manifest.Tokens, "TextPrimary", defaultText);
        return (bg, dock, accent, text);

        static Color TryReadColor(IReadOnlyDictionary<string, System.Text.Json.JsonElement>? primary,
            IReadOnlyDictionary<string, System.Text.Json.JsonElement>? fallback, string key, Color defaultColor)
        {
            if ((primary is not null && primary.TryGetValue(key, out var elem))
                || (fallback is not null && fallback.TryGetValue(key, out elem)))
            {
                string? hex = null;
                if (elem.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    hex = elem.GetString();
                }
                else if (elem.ValueKind == System.Text.Json.JsonValueKind.Object
                    && elem.TryGetProperty("stops", out var stops)
                    && stops.ValueKind == System.Text.Json.JsonValueKind.Array
                    && stops.GetArrayLength() > 0
                    && stops[0].ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    hex = stops[0].GetString();
                }

                if (hex is not null && hex.StartsWith('#'))
                {
                    try
                    {
                        if (ColorConverter.ConvertFromString(hex) is Color c) return Color.FromRgb(c.R, c.G, c.B);
                    }
                    catch (FormatException) { }
                }
            }
            return defaultColor;
        }
    }

    private static string DescribeThemePack(ThemePack pack)
    {
        var parts = new List<string>();
        if (pack.Icons is not null) parts.Add(UiText.Get("ThemePackIcons"));
        if (pack.Widgets is not null) parts.Add(UiText.Get("ThemePackWidgets"));
        var contents = string.Join(" · ", parts);
        var author = string.IsNullOrWhiteSpace(pack.Manifest.Author) ? string.Empty : " — " + pack.Manifest.Author;
        return contents + author;
    }

    private void OpenGithub_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo("https://github.com/alztrk/opentaskbar")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            AppLog.Warning("Cannot open the OpenTaskbar GitHub page: " + ex.Message);
        }
    }

    private void ThemePackOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(ThemePacks.Root);
            using var _ = Process.Start(new ProcessStartInfo(ThemePacks.Root) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            AppLog.ReportError("ErrorThemeFolderOpen", ex);
        }
    }

    private void ThemePackReload_Click(object sender, RoutedEventArgs e)
    {
        var selected = _selectedThemePackId;
        _initializing = true;
        LoadThemePacks(selected);
        _initializing = false;
        ThemePacks.Select(selected, reload: true);
        ThemePacks.ApplyWidgets(_preferences.Current.Theme);
    }

    private void ThemePackNew_Click(object sender, RoutedEventArgs e)
    {
        var name = TextPrompt.Ask(UiText.Get("ThemePackNew"), UiText.Get("ThemePackNew"), UiText.Get("ThemePackNewDefault"));
        if (string.IsNullOrWhiteSpace(name)) return;
        var id = ThemePackTransfer.CreateTheme(name, null, null);
        ReloadThemePacks(id);
        MessageBox.Show(this, UiText.Get("ThemePackCreated"), "OpenTaskbar", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ThemePackDuplicate_Click(object sender, RoutedEventArgs e)
    {
        var selected = _selectedThemePackId;
        if (string.IsNullOrWhiteSpace(selected)) { AppLog.ReportError("ErrorThemeMissing"); return; }
        var pack = ThemePacks.Discover().FirstOrDefault(p => string.Equals(p.Id, selected, StringComparison.OrdinalIgnoreCase));
        var name = TextPrompt.Ask(UiText.Get("ThemePackDuplicate"), UiText.Get("ThemePackDuplicate"),
            (pack?.DisplayName ?? selected) + " " + UiText.Get("ThemePackCopySuffix"));
        if (string.IsNullOrWhiteSpace(name)) return;
        var id = ThemePackTransfer.CreateTheme(name, null, selected);
        ReloadThemePacks(id);
        MessageBox.Show(this, UiText.Get("ThemePackDuplicated"), "OpenTaskbar", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ThemePackExport_Click(object sender, RoutedEventArgs e)
    {
        var selected = _selectedThemePackId;
        if (string.IsNullOrWhiteSpace(selected)) { AppLog.ReportError("ErrorThemeMissing"); return; }
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = UiText.Get("ThemePackSaveFile"),
            Filter = UiText.Get("ThemePackFileFilter"),
            FileName = selected + ThemePackTransfer.ExportExtension,
            DefaultExt = ThemePackTransfer.ExportExtension
        };
        if (dialog.ShowDialog(this) != true) return;
        var error = ThemePackTransfer.ExportTheme(selected, dialog.FileName);
        if (error is not null) { AppLog.ReportError(error); return; }
        MessageBox.Show(this, UiText.Get("ThemePackExported"), "OpenTaskbar", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ThemePackImport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = UiText.Get("ThemePackChooseFile"),
            Filter = UiText.Get("ThemePackFileFilter"),
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        var (id, error) = ThemePackTransfer.ImportTheme(dialog.FileName);
        if (error is not null) { AppLog.ReportError(error); return; }
        ReloadThemePacks(id);
        MessageBox.Show(this, UiText.Get("ThemePackImported"), "OpenTaskbar", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ThemePackDelete_Click(object sender, RoutedEventArgs e)
    {
        var selected = _selectedThemePackId;
        if (string.IsNullOrWhiteSpace(selected)) { AppLog.ReportError("ErrorThemeMissing"); return; }
        var result = MessageBox.Show(this, UiText.Format("ThemePackDeleteConfirm", selected), "OpenTaskbar",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;
        var error = ThemePackTransfer.DeleteTheme(selected);
        if (error is not null) { AppLog.ReportError(error); return; }
        ReloadThemePacks(null);
        MessageBox.Show(this, UiText.Get("ThemePackDeleted"), "OpenTaskbar", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ReloadThemePacks(string? selected)
    {
        var wasInitializing = _initializing;
        _initializing = true;
        try { LoadThemePacks(selected); }
        finally { _initializing = wasInitializing; }
        ThemePacks.Select(selected, reload: true);
        ThemePacks.ApplyWidgets(_preferences.Current.Theme);
        // New, duplicated, and imported packs should remain selected after restart.
        if (!wasInitializing) TriggerAutoSave(immediate: true);
    }

    private SystemMeters ReadStatsMeters() =>
        (StatsCpuToggle.IsChecked == true ? SystemMeters.Cpu : 0)
        | (StatsMemoryToggle.IsChecked == true ? SystemMeters.Memory : 0)
        | (StatsGpuToggle.IsChecked == true ? SystemMeters.Gpu : 0)
        | (StatsGpuTemperatureToggle.IsChecked == true ? SystemMeters.GpuTemperature : 0);

    private void RefreshWidgetPreviews() => DockWidgetService.Refresh(
        _widgetOptions.Select(option => option.Preview), _preferences.Current.Language);

    private void WidgetPreviewTimer_Tick(object? sender, EventArgs e) => RefreshWidgetPreviews();

    private void AutoSaveTimer_Tick(object? sender, EventArgs e)
    {
        _autoSaveTimer.Stop();
        SaveSettings();
    }

    private void CustomThemeEditor_ThemeSaved(object? sender, EventArgs e) => BuildThemeGallery(_selectedThemePackId);

    private void CustomThemeEditor_ThemeCreated(object? sender, string id) => ReloadThemePacks(id);

    private void Settings_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateWidgetPreviewRefresh();

    private void SettingsTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, SettingsTabs))
        {
            CancelHotkeyRecording();
        }
        if (!_initializing && ReferenceEquals(e.OriginalSource, SettingsTabs))
        {
            if (!string.IsNullOrWhiteSpace(SettingsSearchBox.Text))
            {
                SettingsSearchBox.Text = string.Empty;
            }
            ClearAutoFocusedPageControl();
            UpdateWidgetPreviewRefresh();
            if (_preferences.Current.EffectiveAnimationsEnabled && SettingsTabs.Template?.FindName("PART_SelectedContentHost", SettingsTabs) is ContentPresenter host)
            {
                var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
                easing.Freeze();

                var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = easing };
                fade.Completed += (_, _) => host.BeginAnimation(UIElement.OpacityProperty, null);
                host.BeginAnimation(UIElement.OpacityProperty, fade);

                // A short upward slide gives page changes a sense of direction without moving the sidebar.
                var transform = host.RenderTransform as TranslateTransform ?? new TranslateTransform();
                host.RenderTransform = transform;
                var slide = new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = easing };
                slide.Completed += (_, _) =>
                {
                    transform.BeginAnimation(TranslateTransform.YProperty, null);
                    transform.Y = 0;
                };
                transform.BeginAnimation(TranslateTransform.YProperty, slide);
            }
        }
    }

    // Switching TabControl pages disconnects the previously focused element and causes WPF to
    // auto-focus the first focusable button/input on the newly shown page. Clear that incidental
    // content focus while keeping intentional TabItem keyboard navigation intact.
    private void ClearAutoFocusedPageControl()
    {
        void ClearIfContentControl()
        {
            if (_closed) return;
            if (Keyboard.FocusedElement is TabItem) return;
            if (ReferenceEquals(Keyboard.FocusedElement, SettingsSearchBox)) return;
            Keyboard.ClearFocus();
            FocusManager.SetFocusedElement(this, null);
        }

        ClearIfContentControl();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ClearIfContentControl);
    }

    private void UpdateWidgetPreviewRefresh()
    {
        if (IsVisible && WidgetsTab.IsSelected)
        {
            if (!_livePreviewsAttached && !_closed)
            {
                AttachLiveWidgetPreviews();
            }
            RefreshWidgetPreviews();
            _widgetPreviewTimer.Start();
            _livePreviewTimer?.Start();
        }
        else
        {
            _widgetPreviewTimer.Stop();
            _livePreviewTimer?.Stop();
        }
    }

    /// <summary>
    /// Replaces the catalog's static mock visuals with real, running dock widgets. Each preview is a genuine
    /// widget instance with current settings applied, so the cards show exactly what the taskbar shows.
    /// </summary>
    private void AttachLiveWidgetPreviews()
    {
        StopAndDisposeLivePreviews();
        _livePreviewsAttached = true;
        var currentSettings = _autoSaveTimer.IsEnabled ? ReadSelection() : _preferences.Current;
        foreach (var option in _widgetOptions)
        {
            var mode = ResolveDensityMode(currentSettings, option.Id);
            option.Preview.IsCompact = mode == WidgetDensityMode.Compact;
        }
        // Capability comes from the registry: legacy timed tiles have no live DockWidget instance to attach.
        foreach (var option in _widgetOptions.Where(o =>
                     DockWidgetService.TryGetDescriptor(o.Id, out var descriptor) && descriptor.SupportsLivePreview))
        {
            OpenTaskbar.Widgets.DockWidget? widget = null;
            try
            {
                widget = WidgetCoordinator.CreateWidget(option.Id);
                widget.ReportError = message => AppLog.Warning($"Widget preview '{option.Id}': {message}");
                widget.ApplySettings(currentSettings);
                widget.Activate();
                var mode = ResolveDensityMode(currentSettings, option.Id);
                widget.SetCompactState(widget.SupportsCompactMode && mode == WidgetDensityMode.Compact);
                widget.Tick(DateTime.Now);
                // Reflect hardware availability (e.g. a desktop without a battery) in the catalog.
                option.IsHardwareAvailable = widget.IsHardwareAvailable;
                widget.HardwareAvailabilityChanged += (_, _) => option.IsHardwareAvailable = widget.IsHardwareAvailable;
                // An inactive data source (no media session, no providers signed in) must not blank the card.
                widget.Visibility = widget.IsHardwareAvailable ? Visibility.Visible : Visibility.Collapsed;
                option.LivePreview = widget;
                option.NativePreview = widget;
                // Build the flyout panel once; widget settings changes refresh it below.
                option.PreviewFlyoutContent = widget.BuildPreviewFlyoutContent();
                _livePreviewWidgets.Add(widget);
            }
            catch (Exception ex)
            {
                if (widget is not null && !_livePreviewWidgets.Contains(widget))
                {
                    widget.Dispose();
                }
                AppLog.Warning($"Widget preview '{option.Id}' unavailable: {ex.Message}");
            }
        }

        _livePreviewTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _livePreviewTimer.Tick += (_, _) =>
        {
            var now = DateTime.Now;
            foreach (var widget in _livePreviewWidgets)
            {
                widget.Tick(now);
                if (widget.IsHardwareAvailable && widget.Visibility != Visibility.Visible)
                    widget.Visibility = Visibility.Visible;
            }
        };
        if (IsVisible && WidgetsTab.IsSelected)
        {
            _livePreviewTimer.Start();
        }
    }

    /// <summary>
    /// Pushes the current settings into every catalog preview immediately. The previews are genuine
    /// widget instances, so this re-renders them exactly as the taskbar widgets would after a save.
    /// </summary>
    private void ApplySettingsToLivePreviews(DockSettings settings)
    {
        foreach (var option in _widgetOptions)
        {
            var mode = ResolveDensityMode(settings, option.Id);
            option.Preview.IsCompact = mode == WidgetDensityMode.Compact;

            if (_livePreviewsAttached && option.LivePreview is OpenTaskbar.Widgets.DockWidget widget)
            {
                widget.ApplySettings(settings);
                widget.SetCompactState(widget.SupportsCompactMode && mode == WidgetDensityMode.Compact);
                // Some widgets hide themselves when their data source is inactive (e.g. media with no
                // session). A preview must stay visible so the card never collapses to an empty tile.
                if (widget.IsHardwareAvailable && widget.Visibility != Visibility.Visible)
                    widget.Visibility = Visibility.Visible;
                option.IsHardwareAvailable = widget.IsHardwareAvailable;
                // Rebuild the inline flyout preview so it reflects the just-applied settings.
                if (option.ShowFlyoutPreview) option.PreviewFlyoutContent = widget.BuildPreviewFlyoutContent();
            }

            if (option.NativePreview is OpenTaskbar.Widgets.DockWidget native && !ReferenceEquals(native, option.LivePreview))
            {
                native.ApplySettings(settings);
                native.SetCompactState(native.SupportsCompactMode && mode == WidgetDensityMode.Compact);
                if (native.SupportsStaticPreview) native.RenderPreview();
            }
        }
    }

    private static WidgetDensityMode ResolveDensityMode(DockSettings settings, string _) => settings.GlobalWidgetDensity;

    private void StopAndDisposeLivePreviews()
    {
        _livePreviewsAttached = false;
        _livePreviewTimer?.Stop();
        _livePreviewTimer = null;
        var liveWidgets = new HashSet<OpenTaskbar.Widgets.DockWidget>(_livePreviewWidgets);
        foreach (var widget in _livePreviewWidgets) widget.Dispose();
        _livePreviewWidgets.Clear();
        foreach (var option in _widgetOptions)
        {
            option.LivePreview = null;
            // Detach the disposed live widget so callers (and the Closed handler) never touch it again.
            // Any other standalone preview widget is ours to dispose here.
            if (option.NativePreview is OpenTaskbar.Widgets.DockWidget standalone && !liveWidgets.Contains(standalone))
                standalone.Dispose();
            if (option.NativePreview is OpenTaskbar.Widgets.DockWidget) option.NativePreview = option.Preview;
        }
    }

    private void WidgetInstallation_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement { DataContext: DockWidgetOption option }) return;
        _initializing = true;
        if (option.IsInstalled)
        {
            option.IsInstalled = false;
            option.IsEnabled = false;
            option.IsSettingsExpanded = false;
        }
        else
        {
            option.IsInstalled = true;
            option.IsEnabled = true;
        }
        _initializing = false;
        UpdateWidgetOptionState();
        SaveSettings();
    }

    private void WidgetSettings_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement { DataContext: DockWidgetOption option }) return;
        var expand = !option.IsSettingsExpanded;
        foreach (var other in _widgetOptions)
            other.IsSettingsExpanded = ReferenceEquals(other, option) && expand;
        if (expand && ReferenceEquals(option.NativePreview, option.Preview))
        {
            // Prefer the already-running live widget so the drawer shows exactly what the dock shows.
            var live = option.LivePreview as OpenTaskbar.Widgets.DockWidget;
            var native = live ?? WidgetCoordinator.CreatePreviewWidget(option.Id);
            if (native is not null)
            {
                var currentSettings = ReadSelection();
                native.ApplySettings(currentSettings);
                native.SetCompactState(native.SupportsCompactMode && currentSettings.GlobalWidgetDensity == WidgetDensityMode.Compact);
                native.Visibility = Visibility.Visible;
                // Only widgets with local-only rendering paint an extra frame when they are not
                // already activated; the rest keep their constructor's honest placeholder state.
                if (live is null && native.SupportsStaticPreview) native.RenderPreview();
                option.NativePreview = native;
            }
            else
            {
                option.NativePreview = option.Preview;
            }
        }
        UpdateWidgetOptionState();
        WidgetOptionsList.InvalidateMeasure();
    }

    private void CollapseClockOptions_Click(object sender, RoutedEventArgs e)
    {
        if (_widgetOptions.FirstOrDefault(o => o.Id == "Clock") is { } option)
            option.IsSettingsExpanded = false;
        UpdateWidgetOptionState();
    }

    private void CollapseWeatherOptions_Click(object sender, RoutedEventArgs e)
    {
        if (_widgetOptions.FirstOrDefault(o => o.Id == "Weather") is { } option)
            option.IsSettingsExpanded = false;
        UpdateWidgetOptionState();
    }

    private void CollapseFocusOptions_Click(object sender, RoutedEventArgs e)
    {
        if (_widgetOptions.FirstOrDefault(o => o.Id == "Focus") is { } option)
            option.IsSettingsExpanded = false;
        UpdateWidgetOptionState();
    }

    private void CollapseStatsOptions_Click(object sender, RoutedEventArgs e)
    {
        if (_widgetOptions.FirstOrDefault(o => o.Id == "SystemStats") is { } option)
            option.IsSettingsExpanded = false;
        UpdateWidgetOptionState();
    }

    private void SelectAlignment(DockSettings settings)
    {
        SelectChoice(PositionBox, settings.Layout == DockLayout.FullWidth ? "FullWidth" : settings.Position.ToString());
        SelectChoice(ContentAlignmentBox, settings.ContentAlignment.ToString());
    }

    private static void OpenActions(object sender)
    {
        if (sender is Button button && button.ContextMenu is { } menu)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    private void ThemePackActions_Click(object sender, RoutedEventArgs e) => OpenActions(sender);

    private void BackupActions_Click(object sender, RoutedEventArgs e) => OpenActions(sender);

    private void LoadFeatureSelection(DockSettings settings)
    {
        RecycleBinToggle.IsChecked = settings.ShowRecycleBin;
        SearchButtonToggle.IsChecked = settings.ShowSearchButton;
        TaskViewButtonToggle.IsChecked = settings.ShowTaskViewButton;
        TrayButtonToggle.IsChecked = settings.ShowTrayButton;
        ShowDesktopButtonToggle.IsChecked = settings.ShowDesktopButton;
        SelectChoice(DensityBox, settings.GlobalWidgetDensity.ToString());
        LoadDisplayChoices(settings.TargetDisplay);
        AllMonitorsToggle.IsChecked = settings.ShowOnAllMonitors;
        LoadSecondaryAppChoices(settings.SecondaryApps);
        SecondaryWidgetsToggle.IsChecked = settings.SecondaryWidgets;
        MirrorMainDockToggle.IsChecked = settings.MirrorMainDock;
        RevealOnWinKeyToggle.IsChecked = settings.RevealDockOnWinKey;
        UpdateMultiMonitorControls();
        UpdateAutoHideControls();
        CurrentDesktopToggle.IsChecked = settings.CurrentDesktopOnly;
        UpdatesToggle.IsChecked = settings.CheckUpdates;
        FocusModeToggle.IsChecked = settings.FocusModeEnabled;
        DynamicIslandEnabledToggle.IsChecked = settings.DynamicIslandEnabled;
        LoadDynamicIslandPlacementChoices(settings);
        SelectChoice(DynamicIslandVisibilityBox, settings.DynamicIslandVisibility.ToString());
        DynamicIslandHideFullscreenToggle.IsChecked = settings.DynamicIslandHideInFullscreen;
        DynamicIslandAutoHideToggle.IsChecked = settings.DynamicIslandAutoHide;
        LoadDynamicIslandAppearance(settings);
        UpdateDynamicIslandControls();
        SelectChoice(EdgeBox, settings.Edge.ToString());
        SelectChoice(ClockFormatBox, settings.Clock24Hour ? "24" : "12");
        ClockSecondsToggle.IsChecked = settings.ClockShowSeconds;
        SelectChoice(DateStyleBox, settings.DateStyle.ToString());
        SelectChoice(DateFormatBox, settings.DateFormat.ToString());
        SelectChoice(ClockDateLayoutBox, settings.ClockDateLayout.ToString());
        SelectChoice(WeatherUnitBox, settings.WeatherUnit.ToString());
        WeatherCityBox.Text = settings.WeatherCity;
        _aiUsageRing = settings.AiUsageRing;
        foreach (var combo in _aiRingBoxes)
        {
            foreach (var item in combo.Items)
            {
                if (item is ComboBoxItem cbi && string.Equals(cbi.Tag as string, _aiUsageRing.ToString(), StringComparison.Ordinal))
                {
                    combo.SelectedItem = cbi;
                    break;
                }
            }
        }
        _antigravityGroups = (settings.AntigravityGroups & AntigravityGroups.All) == AntigravityGroups.None
            ? AntigravityGroups.All
            : (settings.AntigravityGroups & AntigravityGroups.All);
        UpdateAntigravityGroupCheckboxes();
        StatsCpuToggle.IsChecked = settings.StatsMeters.HasFlag(SystemMeters.Cpu);
        StatsMemoryToggle.IsChecked = settings.StatsMeters.HasFlag(SystemMeters.Memory);
        StatsGpuToggle.IsChecked = settings.StatsMeters.HasFlag(SystemMeters.Gpu);
        StatsGpuTemperatureToggle.IsChecked = settings.StatsMeters.HasFlag(SystemMeters.GpuTemperature);
        _recordingHotkeyTarget = HotkeyTarget.None;
        ClearHotkeyValidation();
        // Legacy WinAltD cannot be registered (MOD_WIN is intentionally rejected), so upgrade it
        // to Ctrl+Alt+D when no custom binding overrides it.
        _dockHotkeyChoice = settings.DockHotkey == HotkeyChoice.WinAltD && settings.CustomDockHotkey is null
            ? HotkeyChoice.CtrlAltD
            : settings.DockHotkey;
        _dockCustomHotkey = settings.CustomDockHotkey;
        _launcherHotkeyChoice = settings.LauncherHotkey;
        _launcherCustomHotkey = settings.CustomLauncherHotkey;
        UpdateHotkeyControls();
        SelectChoice(StartPlacementBox, settings.StartPlacement.ToString());
        WheelVolumeToggle.IsChecked = settings.WheelVolumeEnabled;
        _ = InitializeWeatherLocationsAsync(settings.WeatherCity);
    }

    private void Settings_Deactivated(object? sender, EventArgs e) => CancelHotkeyRecording();

    private void UpdateHotkeyControls()
    {
        DockHotkeyCaptureButton.Content = _recordingHotkeyTarget == HotkeyTarget.Dock
            ? UiText.Get("HotkeyRecording")
            : HotkeyBinding.Resolve(_dockHotkeyChoice, _dockCustomHotkey)?.DisplayText ?? UiText.Get("HotkeyDisabled");
        DockHotkeyCaptureButton.SetResourceReference(
            Control.ForegroundProperty, _recordingHotkeyTarget == HotkeyTarget.Dock ? "AccentBrush" : "TextPrimary");
        LauncherHotkeyCaptureButton.Content = _recordingHotkeyTarget == HotkeyTarget.Launcher
            ? UiText.Get("HotkeyRecording")
            : HotkeyBinding.Resolve(_launcherHotkeyChoice, _launcherCustomHotkey)?.DisplayText ?? UiText.Get("HotkeyDisabled");
        LauncherHotkeyCaptureButton.SetResourceReference(
            Control.ForegroundProperty, _recordingHotkeyTarget == HotkeyTarget.Launcher ? "AccentBrush" : "TextPrimary");
        DockHotkeyResetButton.IsEnabled = _dockCustomHotkey is not null || _dockHotkeyChoice != HotkeyChoice.CtrlAltD;
        LauncherHotkeyResetButton.IsEnabled = _launcherCustomHotkey is not null || _launcherHotkeyChoice != HotkeyChoice.AltSpace;
    }

    private void ClearHotkeyValidation()
    {
        HotkeyValidationText.Text = string.Empty;
        HotkeyValidationText.Visibility = Visibility.Collapsed;
    }

    private void ShowHotkeyValidation(string message)
    {
        HotkeyValidationText.Text = message;
        HotkeyValidationText.Visibility = Visibility.Visible;
    }

    private void CancelHotkeyRecording()
    {
        if (_recordingHotkeyTarget == HotkeyTarget.None) return;
        _recordingHotkeyTarget = HotkeyTarget.None;
        ClearHotkeyValidation();
        UpdateHotkeyControls();
    }

    private void HotkeyCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        var target = sender == DockHotkeyCaptureButton ? HotkeyTarget.Dock : HotkeyTarget.Launcher;
        _recordingHotkeyTarget = _recordingHotkeyTarget == target ? HotkeyTarget.None : target;
        if (_recordingHotkeyTarget == HotkeyTarget.Dock)
            DockHotkeyCaptureButton.Focus();
        else if (_recordingHotkeyTarget == HotkeyTarget.Launcher)
            LauncherHotkeyCaptureButton.Focus();
        ClearHotkeyValidation();
        UpdateHotkeyControls();
    }

    private void HotkeyResetButton_Click(object sender, RoutedEventArgs e)
    {
        _recordingHotkeyTarget = HotkeyTarget.None;
        if (sender == DockHotkeyResetButton)
        {
            var binding = HotkeyBinding.FromChoice(HotkeyChoice.CtrlAltD);
            var otherBinding = HotkeyBinding.Resolve(_launcherHotkeyChoice, _launcherCustomHotkey);
            if (HotkeyBinding.EqualsBinding(binding, otherBinding))
            {
                ShowHotkeyValidation(UiText.Format("HotkeyConflict", UiText.Get("LauncherHotkey")));
                return;
            }
            _dockHotkeyChoice = HotkeyChoice.CtrlAltD;
            _dockCustomHotkey = null;
        }
        else
        {
            var binding = HotkeyBinding.FromChoice(HotkeyChoice.AltSpace);
            var otherBinding = HotkeyBinding.Resolve(_dockHotkeyChoice, _dockCustomHotkey);
            if (HotkeyBinding.EqualsBinding(binding, otherBinding))
            {
                ShowHotkeyValidation(UiText.Format("HotkeyConflict", UiText.Get("DockHotkey")));
                return;
            }
            _launcherHotkeyChoice = HotkeyChoice.AltSpace;
            _launcherCustomHotkey = null;
        }

        ClearHotkeyValidation();
        UpdateHotkeyControls();
        TriggerAutoSave(immediate: true);
    }

    private void AttachWidgetSettingsPanels()
    {
        WidgetPanelsPool.Children.Clear();
        _aiRingBoxes.Clear();
        _antigravityGroupBoxes.Clear();
        _aiAccountRefreshers.Clear();
        if (_widgetOptions.FirstOrDefault(o => o.Id == "Clock") is { } clock)
        {
            clock.SettingsView = ClockOptions;
        }
        if (_widgetOptions.FirstOrDefault(o => o.Id == "Weather") is { } weather)
        {
            weather.SettingsView = WeatherOptions;
        }
        if (_widgetOptions.FirstOrDefault(o => o.Id == "Focus") is { } focus)
        {
            focus.SettingsView = FocusOptions;
        }
        if (_widgetOptions.FirstOrDefault(o => o.Id == "SystemStats") is { } stats)
        {
            stats.SettingsView = StatsOptions;
        }
        var aiUsageById = DockWidgetService.AiUsageWidgets.ToDictionary(w => w.Id, w => w.Provider, StringComparer.Ordinal);
        foreach (var option in _widgetOptions.Where(o => o.SettingsView is null))
        {
            var panel = new StackPanel();
            if (aiUsageById.TryGetValue(option.Id, out var provider))
            {
                AttachAiRingRow(panel);
                if (provider == AiProviders.Cursor)
                {
                    AttachCursorAuthSection(panel);
                }
                else
                {
                    AttachAiAccountSection(option.Id, provider, panel);
                }
            }
            if (option.Id == "AiUsageAntigravity")
            {
                AttachAntigravityGroupsRow(panel);
            }
            option.SettingsView = panel;
        }
    }

    private void OnAiCredentialsChanged(AiProviders provider)
    {
        if (_closed) return;
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(() => OnAiCredentialsChanged(provider));
            return;
        }
        foreach (var refresh in _aiAccountRefreshers) refresh();
        if (_livePreviewsAttached)
        {
            foreach (var option in _widgetOptions.Where(o => o.LivePreview is OpenTaskbar.Widgets.DockWidget))
            {
                var widget = (OpenTaskbar.Widgets.DockWidget)option.LivePreview!;
                if (option.ShowFlyoutPreview) option.PreviewFlyoutContent = widget.BuildPreviewFlyoutContent();
            }
        }
    }

    private void AttachAiAccountSection(string widgetId, AiProviders provider, Panel panel)
    {
        var section = new Border
        {
            Tag = $"AiAccountSection:{widgetId}",
            BorderThickness = new Thickness(0, 1, 0, 0),
            Margin = new Thickness(0, 4, 0, 0),
            Padding = new Thickness(0, 10, 0, 0)
        };
        section.SetResourceReference(Border.BorderBrushProperty, "StrokeSubtle");

        var container = new StackPanel();
        section.Child = container;

        // 1. Account status row + Refresh button
        var statusRow = new Grid();
        statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var accountLabel = new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };
        accountLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        Grid.SetColumn(accountLabel, 0);
        statusRow.Children.Add(accountLabel);

        var statusRight = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        var statusDot = new Border
        {
            Width = 6,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        statusRight.Children.Add(statusDot);

        var statusText = new TextBlock
        {
            Tag = $"AiAccountStatus:{provider}",
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        statusRight.Children.Add(statusText);

        var refreshBtn = new Button
        {
            Tag = $"AiAccountRefresh:{provider}",
            Padding = new Thickness(10, 4, 10, 4),
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center
        };
        refreshBtn.SetResourceReference(StyleProperty, "SettingsButton");
        statusRight.Children.Add(refreshBtn);

        Grid.SetColumn(statusRight, 1);
        statusRow.Children.Add(statusRight);
        container.Children.Add(statusRow);

        TextBlock? cliLabel = null;
        TextBlock? cliHint = null;
        Button? signInBtn = null;
        var hasCliLaunchError = false;

        if (AiUsageService.IsCliSignInProvider(provider))
        {
            var cliRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            cliRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cliRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var cliLeft = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            cliLabel = new TextBlock
            {
                FontSize = 12
            };
            cliLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            cliLeft.Children.Add(cliLabel);

            cliHint = new TextBlock
            {
                Tag = $"AiAccountCliHint:{provider}",
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            cliHint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            cliLeft.Children.Add(cliHint);

            Grid.SetColumn(cliLeft, 0);
            cliRow.Children.Add(cliLeft);

            signInBtn = new Button
            {
                Tag = $"AiAccountSignIn:{provider}",
                Padding = new Thickness(12, 5, 12, 5),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            signInBtn.SetResourceReference(StyleProperty, "SettingsButton");
            Grid.SetColumn(signInBtn, 1);
            cliRow.Children.Add(signInBtn);

            container.Children.Add(cliRow);
        }

        TextBlock? apiKeyLabel = null;
        TextBlock? apiKeyHint = null;
        PasswordBox? apiKeyBox = null;
        Button? saveBtn = null;
        Button? clearBtn = null;

        if (AiUsageService.IsApiKeyProvider(provider))
        {
            var apiBlock = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };

            var headerRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            apiKeyHint = new TextBlock
            {
                Tag = $"AiAccountKeyHint:{provider}",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            apiKeyHint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            DockPanel.SetDock(apiKeyHint, Dock.Right);
            headerRow.Children.Add(apiKeyHint);

            apiKeyLabel = new TextBlock
            {
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            apiKeyLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            headerRow.Children.Add(apiKeyLabel);
            apiBlock.Children.Add(headerRow);

            var inputRow = new Grid();
            inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            apiKeyBox = new PasswordBox
            {
                Tag = $"AiAccountKeyBox:{provider}",
                MinHeight = 30,
                Padding = new Thickness(8, 4, 8, 4),
                FontSize = 12,
                VerticalContentAlignment = VerticalAlignment.Center,
                BorderThickness = new Thickness(1)
            };
            apiKeyBox.SetResourceReference(Control.BackgroundProperty, "SettingsControl");
            apiKeyBox.SetResourceReference(Control.ForegroundProperty, "TextPrimary");
            apiKeyBox.SetResourceReference(Control.BorderBrushProperty, "StrokeSubtle");
            Grid.SetColumn(apiKeyBox, 0);
            inputRow.Children.Add(apiKeyBox);

            saveBtn = new Button
            {
                Tag = $"AiAccountKeySave:{provider}",
                Padding = new Thickness(12, 5, 12, 5),
                FontSize = 12,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            saveBtn.SetResourceReference(StyleProperty, "SettingsButton");
            Grid.SetColumn(saveBtn, 1);
            inputRow.Children.Add(saveBtn);

            clearBtn = new Button
            {
                Tag = $"AiAccountKeyClear:{provider}",
                Padding = new Thickness(10, 5, 10, 5),
                FontSize = 12,
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            clearBtn.SetResourceReference(StyleProperty, "SettingsGhostButton");
            Grid.SetColumn(clearBtn, 2);
            inputRow.Children.Add(clearBtn);

            apiBlock.Children.Add(inputRow);
            container.Children.Add(apiBlock);
        }

        void RefreshAccountUi()
        {
            var connected = AiUsageService.HasCredential(provider);
            var hasSavedKey = AiUsageService.HasSavedSecret(provider);

            accountLabel.Text = UiText.Get("AiUsageAccountLabel");
            statusText.Text = UiText.Get(connected ? "AiUsageAccountConnected" : "AiUsageAccountSignInRequired");
            statusText.SetResourceReference(TextBlock.ForegroundProperty, connected ? "WidgetAccent" : "TextSecondary");
            statusDot.SetResourceReference(Border.BackgroundProperty, connected ? "WidgetAccent" : "TextMuted");
            refreshBtn.Content = UiText.Get("AiUsageRefresh");

            if (cliLabel is not null && cliHint is not null && signInBtn is not null)
            {
                var cmd = AiUsageService.CliSignInCommand(provider) ?? string.Empty;
                cliLabel.Text = UiText.Get("AiUsageCliSignInLabel");
                cliHint.Text = hasCliLaunchError ? UiText.Format("ErrorAiUsageSignIn", cmd) : cmd;
                cliHint.SetResourceReference(TextBlock.ForegroundProperty, hasCliLaunchError ? "WidgetWarning" : "TextSecondary");
                signInBtn.Content = UiText.Get("AiUsageStatusSignIn");
            }

            if (apiKeyLabel is not null && apiKeyHint is not null && saveBtn is not null && clearBtn is not null)
            {
                apiKeyLabel.Text = UiText.Get("AiUsageApiKeyLabel");
                apiKeyHint.Text = UiText.Get(hasSavedKey ? "AiUsageApiKeySaved" : "AiUsageApiKeyPlaceholder");
                apiKeyHint.SetResourceReference(TextBlock.ForegroundProperty, hasSavedKey ? "WidgetAccent" : "TextSecondary");
                saveBtn.Content = UiText.Get("AiUsageApiKeySave");
                clearBtn.Content = UiText.Get("AiUsageApiKeyClear");
                clearBtn.IsEnabled = hasSavedKey;
            }
        }

        refreshBtn.Click += (_, e) =>
        {
            e.Handled = true;
            hasCliLaunchError = false;
            AiUsageService.NotifyCredentialsChanged(provider);
            RefreshAccountUi();
        };

        if (signInBtn is not null)
        {
            signInBtn.Click += (_, e) =>
            {
                e.Handled = true;
                hasCliLaunchError = AiUsageService.TryLaunchSignIn(provider) is not null;
                RefreshAccountUi();
            };
        }

        if (saveBtn is not null && clearBtn is not null && apiKeyBox is not null)
        {
            saveBtn.Click += (_, e) =>
            {
                e.Handled = true;
                var entered = apiKeyBox.Password;
                if (string.IsNullOrWhiteSpace(entered)) return;
                apiKeyBox.Clear();
                AiUsageService.SaveApiKey(provider, entered.Trim());
                RefreshAccountUi();
            };

            clearBtn.Click += (_, e) =>
            {
                e.Handled = true;
                apiKeyBox.Clear();
                AiUsageService.SaveApiKey(provider, null);
                RefreshAccountUi();
            };
        }

        RefreshAccountUi();
        _aiAccountRefreshers.Add(RefreshAccountUi);
        panel.Children.Add(section);
    }

    private void AttachCursorAuthSection(Panel panel)
    {
        var section = new StackPanel
        {
            Name = "CursorAuthSection",
            Tag = "AiAccountSection:AiUsageCursor",
            Margin = new Thickness(0, 8, 0, 0)
        };

        var statusRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var accountLabel = new TextBlock
        {
            Text = UiText.Get("AiUsageCursorAccountLabel"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };
        accountLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        Grid.SetColumn(accountLabel, 0);
        statusRow.Children.Add(accountLabel);

        var statusText = new TextBlock
        {
            Name = "CursorAccountStatusText",
            Tag = "AiAccountStatus:Cursor",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(statusText, 1);
        statusRow.Children.Add(statusText);
        section.Children.Add(statusRow);

        var actions = new WrapPanel { Orientation = Orientation.Horizontal };

        var signInButton = new Button
        {
            Name = "CursorSignInButton",
            Padding = new Thickness(10, 5, 10, 5),
            FontSize = 11.5,
            Margin = new Thickness(0, 0, 6, 4)
        };
        signInButton.SetResourceReference(StyleProperty, "SettingsPrimaryButton");
        signInButton.Click += CursorSignInButton_Click;

        var refreshButton = new Button
        {
            Name = "CursorRefreshButton",
            Tag = "AiAccountRefresh:Cursor",
            Padding = new Thickness(10, 5, 10, 5),
            FontSize = 11.5,
            Margin = new Thickness(0, 0, 6, 4)
        };
        refreshButton.SetResourceReference(StyleProperty, "SettingsGhostButton");
        refreshButton.Click += (_, _) =>
        {
            _cursorSignInTimedOut = false;
            CursorAuthService.RequestRefresh();
            UpdateCursorAuthUi();
        };

        var signOutButton = new Button
        {
            Name = "CursorSignOutButton",
            Padding = new Thickness(10, 5, 10, 5),
            FontSize = 11.5,
            Margin = new Thickness(0, 0, 0, 4)
        };
        signOutButton.SetResourceReference(StyleProperty, "SettingsGhostButton");
        signOutButton.Click += (_, _) =>
        {
            CancelCursorLogin();
            _cursorSignInTimedOut = false;
            CursorAuthService.SignOut();
            UpdateCursorAuthUi();
        };

        actions.Children.Add(signInButton);
        actions.Children.Add(refreshButton);
        actions.Children.Add(signOutButton);
        section.Children.Add(actions);

        _cursorAccountLabel = accountLabel;
        _cursorStatusText = statusText;
        _cursorSignInButton = signInButton;
        _cursorRefreshButton = refreshButton;
        _cursorSignOutButton = signOutButton;

        if (!_cursorAuthSubscribed)
        {
            CursorAuthService.SessionChanged += CursorAuthService_SessionChanged;
            _cursorAuthSubscribed = true;
        }

        UpdateCursorAuthUi();
        panel.Children.Add(section);
    }

    private async void CursorSignInButton_Click(object sender, RoutedEventArgs e)
    {
        if (_closed) return;
        if (_cursorLoginCts is not null)
        {
            CancelCursorLogin();
            _cursorSignInTimedOut = false;
            UpdateCursorAuthUi();
            return;
        }

        _cursorSignInTimedOut = false;
        var (verifier, _, uuid, loginUrl) = CursorAuthService.StartLogin();
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(loginUrl) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Info($"Cursor sign-in browser unavailable: {ex.GetType().Name}");
            return;
        }

        var cts = new CancellationTokenSource();
        _cursorLoginCts = cts;
        UpdateCursorAuthUi();

        var completed = false;
        var canceled = false;
        try
        {
            completed = await CursorAuthService.PollLoginAsync(uuid, verifier, cts.Token);
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }
        catch (Exception ex)
        {
            AppLog.Info($"Cursor sign-in failed: {ex.GetType().Name}");
        }
        finally
        {
            if (ReferenceEquals(_cursorLoginCts, cts))
                _cursorLoginCts = null;
            cts.Dispose();
        }

        if (_closed) return;
        if (!completed && !canceled)
            _cursorSignInTimedOut = true;
        UpdateCursorAuthUi();
    }

    private void CancelCursorLogin()
    {
        var cts = _cursorLoginCts;
        _cursorLoginCts = null;
        if (cts is null) return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    private void CursorAuthService_SessionChanged()
    {
        if (_closed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        void Handle()
        {
            if (_closed) return;
            if (CursorAuthService.HasSession) _cursorSignInTimedOut = false;
            UpdateCursorAuthUi();
        }
        if (Dispatcher.CheckAccess()) Handle();
        else _ = Dispatcher.BeginInvoke(Handle);
    }

    private void UpdateCursorAuthUi()
    {
        if (_closed || _cursorAccountLabel is null || _cursorStatusText is null
            || _cursorSignInButton is null || _cursorRefreshButton is null || _cursorSignOutButton is null)
            return;

        var isPolling = _cursorLoginCts is not null;
        var hasSession = CursorAuthService.HasSession;

        _cursorAccountLabel.Text = UiText.Get("AiUsageCursorAccountLabel");
        if (isPolling)
        {
            _cursorStatusText.Text = UiText.Get("AiUsageCursorWaiting");
            _cursorStatusText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        }
        else if (hasSession)
        {
            _cursorStatusText.Text = UiText.Get("AiUsageCursorConnected");
            _cursorStatusText.SetResourceReference(TextBlock.ForegroundProperty, "WidgetAccent");
        }
        else if (_cursorSignInTimedOut)
        {
            _cursorStatusText.Text = UiText.Get("AiUsageCursorSignInTimedOut");
            _cursorStatusText.SetResourceReference(TextBlock.ForegroundProperty, "WidgetWarning");
        }
        else
        {
            _cursorStatusText.Text = UiText.Get("AiUsageCursorSignInRequired");
            _cursorStatusText.SetResourceReference(TextBlock.ForegroundProperty, "WidgetWarning");
        }

        _cursorSignInButton.Content = UiText.Get(isPolling ? "Cancel" : "AiUsageCursorSignIn");
        _cursorRefreshButton.Content = UiText.Get("AiUsageRefresh");
        _cursorRefreshButton.IsEnabled = hasSession && !isPolling;
        _cursorSignOutButton.Content = UiText.Get("AiUsageCursorSignOut");
        _cursorSignOutButton.IsEnabled = hasSession && !isPolling;
    }
    private void AttachAiRingRow(Panel panel)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, panel.Children.Count > 0 ? 8 : 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = UiText.Get("AiUsageRingLabel"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        Grid.SetColumn(label, 0);
        row.Children.Add(label);

        var combo = new ComboBox { MinWidth = 128 };
        combo.Items.Add(new ComboBoxItem { Content = UiText.Get("AiUsageRingHighest"), Tag = nameof(AiUsageRing.Highest) });
        combo.Items.Add(new ComboBoxItem { Content = UiText.Get("AiUsageRingSession"), Tag = nameof(AiUsageRing.Session) });
        combo.Items.Add(new ComboBoxItem { Content = UiText.Get("AiUsageRingWeekly"), Tag = nameof(AiUsageRing.Weekly) });

        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => string.Equals(item.Tag as string, _aiUsageRing.ToString(), StringComparison.Ordinal))
            ?? combo.Items[0];

        combo.SelectionChanged += (_, _) =>
        {
            if (!_initializing && combo.SelectedItem is ComboBoxItem { Tag: string tag } && Enum.TryParse<AiUsageRing>(tag, out var ring))
            {
                _aiUsageRing = ring;
                var wasInitializing = _initializing;
                _initializing = true;
                try
                {
                    foreach (var other in _aiRingBoxes)
                    {
                        if (!ReferenceEquals(other, combo))
                            SelectChoice(other, tag);
                    }
                }
                finally
                {
                    _initializing = wasInitializing;
                }
                ApplySettingsToLivePreviews(ReadSelection());
                TriggerAutoSave(immediate: true);
            }
        };
        Grid.SetColumn(combo, 1);
        row.Children.Add(combo);
        _aiRingBoxes.Add(combo);
        panel.Children.Add(row);
    }

    private void AttachAntigravityGroupsRow(Panel panel)
    {
        var row = new Grid { Margin = new Thickness(0, panel.Children.Count > 0 ? 4 : 0, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = UiText.Get("AiUsageAntigravityGroupsLabel"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        Grid.SetColumn(label, 0);
        row.Children.Add(label);

        var checksPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        foreach (var (group, name) in new[]
        {
            (AntigravityGroups.Gemini, "Gemini"),
            (AntigravityGroups.Claude, "Claude"),
            (AntigravityGroups.Argon, "Argon")
        })
        {
            var box = new CheckBox
            {
                Content = name,
                Tag = $"AntigravityGroup:{name}",
                IsChecked = _antigravityGroups.HasFlag(group),
                VerticalAlignment = VerticalAlignment.Center
            };
            box.SetResourceReference(StyleProperty, "SettingsCheckbox");
            System.Windows.Automation.AutomationProperties.SetName(box, name);

            void HandleToggle(object sender, RoutedEventArgs e)
            {
                e.Handled = true;
                if (_initializing) return;
                var next = ReadAntigravityGroupsFromBoxes();
                if (next == AntigravityGroups.None)
                {
                    var wasInitializing = _initializing;
                    _initializing = true;
                    try
                    {
                        box.IsChecked = true;
                    }
                    finally
                    {
                        _initializing = wasInitializing;
                    }
                    next = ReadAntigravityGroupsFromBoxes();
                    if (next == AntigravityGroups.None) next = group;
                    _antigravityGroups = next;
                    return;
                }
                _antigravityGroups = next;
                ApplySettingsToLivePreviews(ReadSelection());
                TriggerAutoSave(immediate: true);
            }

            box.Checked += HandleToggle;
            box.Unchecked += HandleToggle;
            _antigravityGroupBoxes[group] = box;
            checksPanel.Children.Add(box);
        }

        Grid.SetColumn(checksPanel, 1);
        row.Children.Add(checksPanel);
        panel.Children.Add(row);
    }

    private AntigravityGroups ReadAntigravityGroupsFromBoxes()
    {
        var flags = AntigravityGroups.None;
        foreach (var (group, box) in _antigravityGroupBoxes)
        {
            if (box.IsChecked == true) flags |= group;
        }
        return flags;
    }

    private void UpdateAntigravityGroupCheckboxes()
    {
        foreach (var (group, box) in _antigravityGroupBoxes)
        {
            box.IsChecked = _antigravityGroups.HasFlag(group);
        }
    }

    private async Task InitializeWeatherLocationsAsync(string savedCity)
    {
        try
        {
            ApplyWeatherLocationSelection(savedCity, CountryCityService.GetInitialCountries());
            var countries = await CountryCityService.GetCountriesAsync();
            if (_closed) return;
            ApplyWeatherLocationSelection(savedCity, countries);
        }
        catch (Exception ex)
        {
            AppLog.Warning($"Weather location initialization error: {ex.Message}");
        }
    }

    private void ApplyWeatherLocationSelection(string savedCity, IReadOnlyList<CountryItem> countries)
    {
        var (matchedCountry, matchedCities) = CountryCityService.FindCachedCountryForCity(savedCity);

        var wasInitializing = _initializing;
        _initializing = true;
        _initializingWeather = true;
        try
        {
            WeatherCountryBox.ItemsSource = countries.Select(c => c.Name).ToList();

            if (!string.IsNullOrWhiteSpace(savedCity))
            {
                WeatherCityContainer.Visibility = Visibility.Visible;
                if (matchedCountry is not null)
                {
                    var canonicalCity = matchedCities.FirstOrDefault(c => string.Equals(c, savedCity, StringComparison.OrdinalIgnoreCase)) ?? savedCity;
                    WeatherCountryBox.SelectedItem = matchedCountry;
                    WeatherCountryBox.Text = matchedCountry;
                    WeatherCityCombo.ItemsSource = matchedCities;
                    WeatherCityCombo.SelectedItem = canonicalCity;
                }
                else
                {
                    WeatherCountryBox.SelectedItem = null;
                    WeatherCountryBox.Text = string.Empty;
                    WeatherCityCombo.ItemsSource = Array.Empty<string>();
                    WeatherCityCombo.SelectedItem = null;
                }
                WeatherCityCombo.Text = savedCity;
                WeatherCityBox.Text = savedCity;
            }
            else
            {
                WeatherCountryBox.SelectedItem = null;
                WeatherCountryBox.Text = string.Empty;
                WeatherCityCombo.ItemsSource = Array.Empty<string>();
                WeatherCityCombo.SelectedItem = null;
                WeatherCityCombo.Text = string.Empty;
                WeatherCityBox.Text = string.Empty;
                WeatherCityContainer.Visibility = Visibility.Collapsed;
            }
        }
        finally
        {
            _initializingWeather = false;
            _initializing = wasInitializing;
        }
    }

    private int _weatherCityRequestVersion;

    private async void WeatherCountryBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _initializingWeather) return;
        if (WeatherCountryBox.SelectedItem is not string country) return;
        try { await OnCountrySelectedAsync(country); }
        catch (Exception ex) { AppLog.Warning($"Weather city load failed: {ex.Message}"); }
    }

    private async void WeatherCountryBox_KeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Tab)
        {
            var country = WeatherCountryBox.Text.Trim();
            try { await OnCountrySelectedAsync(country); }
            catch (Exception ex) { AppLog.Warning($"Weather city load failed: {ex.Message}"); }
        }
    }

    private async Task OnCountrySelectedAsync(string country)
    {
        var requestVersion = ++_weatherCityRequestVersion;
        if (string.IsNullOrWhiteSpace(country))
        {
            var wasInit = _initializing;
            _initializing = true;
            try
            {
                WeatherCityContainer.Visibility = Visibility.Collapsed;
                WeatherCityCombo.SelectedItem = null;
                WeatherCityCombo.Text = string.Empty;
                WeatherCityBox.Text = string.Empty;
            }
            finally
            {
                _initializing = wasInit;
            }
            SaveSettings();
            return;
        }

        WeatherCityContainer.Visibility = Visibility.Visible;
        var cities = await CountryCityService.GetCitiesAsync(country);
        if (_closed || requestVersion != _weatherCityRequestVersion) return;
        var previousCity = WeatherCityBox.Text;
        var wasInitializing = _initializing;
        _initializing = true;
        try
        {
            WeatherCityCombo.ItemsSource = cities;
            if (!string.IsNullOrWhiteSpace(previousCity)
                && cities.FirstOrDefault(c => string.Equals(c, previousCity, StringComparison.OrdinalIgnoreCase)) is { } match)
            {
                WeatherCityCombo.SelectedItem = match;
                WeatherCityCombo.Text = match;
            }
            else
            {
                WeatherCityCombo.SelectedItem = null;
                WeatherCityCombo.Text = previousCity;
            }
        }
        finally
        {
            _initializing = wasInitializing;
        }
        if (cities.Count > 0 && IsVisible)
        {
            WeatherCityCombo.IsDropDownOpen = true;
        }
    }

    private void WeatherCityCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        var city = WeatherCityCombo.SelectedItem as string;
        if (!string.IsNullOrWhiteSpace(city))
        {
            WeatherCityBox.Text = city;
            PreferenceEdited(sender, e);
        }
    }

    private void WeatherCityCombo_KeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Tab)
        {
            var city = WeatherCityCombo.Text.Trim();
            if (!string.IsNullOrWhiteSpace(city))
            {
                WeatherCityBox.Text = city;
                PreferenceEdited(sender, e);
            }
        }
    }

    private void WeatherCityCombo_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        var city = WeatherCityCombo.Text.Trim();
        if (!string.Equals(WeatherCityBox.Text, city, StringComparison.Ordinal))
        {
            WeatherCityBox.Text = city;
            TriggerAutoSave(immediate: true);
        }
        else if (_autoSaveTimer.IsEnabled)
        {
            _autoSaveTimer.Stop();
            SaveSettings();
        }
    }

    private void WeatherCityBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => PreferenceEdited(sender, e);

    private void WeatherCityBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_autoSaveTimer.IsEnabled)
        {
            _autoSaveTimer.Stop();
            SaveSettings();
        }
    }

    private void ExportSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = "OpenTaskbar-settings.json",
            Filter = "OpenTaskbar (*.json)|*.json",
            DefaultExt = ".json"
        };
        if (dialog.ShowDialog(this) != true) return;
        var error = SettingsBackup.Export(dialog.FileName, _preferences.Current);
        if (error is not null)
            AppLog.Error($"Settings export: {error}");
        else
            MessageBox.Show(this, UiText.Get("ExportDone"), "OpenTaskbar", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ImportSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "OpenTaskbar (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        var (settings, pins, error) = SettingsBackup.Read(dialog.FileName);
        if (settings is null)
        {
            AppLog.ReportError(error ?? "ErrorSettingsFileInvalid");
            return;
        }

        if (_preferences.Save(settings with { PinOrder = settings.PinOrder ?? _preferences.Current.PinOrder }) is { } saveError)
        {
            AppLog.ReportError(saveError);
            return;
        }
        var added = (Application.Current.MainWindow as MainWindow)?.ImportPins(pins) ?? 0;

        _applied = _preferences.Current;
        _initializing = true;
        LoadAllSelections(_applied);
        _initializing = false;
        UpdateWidgetOptionState();
        MessageBox.Show(this, UiText.Format("ImportDone", added), "OpenTaskbar", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ExportFullBackup_Click(object sender, RoutedEventArgs e)
    {
        FlushPendingAutoSaves();
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = "OpenTaskbar-backup" + FullBackupArchive.ExportExtension,
            Filter = UiText.Get("FullBackupFileFilter"),
            DefaultExt = FullBackupArchive.ExportExtension
        };
        if (dialog.ShowDialog(this) != true) return;
        var error = FullBackupArchive.Export(dialog.FileName, _preferences.Current);
        if (error is not null)
            AppLog.ReportError(error);
        else
            MessageBox.Show(this, UiText.Get("FullBackupExportDone"), "OpenTaskbar", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ImportFullBackup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = UiText.Get("FullBackupFileFilter"),
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        var (preview, inspectError) = FullBackupArchive.Inspect(dialog.FileName);
        if (preview is null || inspectError is not null)
        {
            AppLog.ReportError(inspectError ?? "ErrorFullBackupInvalid");
            return;
        }

        var restoreTheme = false;
        if (preview.HasTheme)
        {
            var result = MessageBox.Show(this, UiText.Format("FullBackupRestoreThemePrompt", preview.ThemeName ?? string.Empty),
                "OpenTaskbar", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (result == MessageBoxResult.Cancel) return;
            restoreTheme = result == MessageBoxResult.Yes;
        }

        var (settings, pins, importedThemeId, importError) = FullBackupArchive.Import(dialog.FileName, restoreTheme);
        if (settings is null || importError is not null)
        {
            AppLog.ReportError(importError ?? "ErrorFullBackupInvalid");
            return;
        }

        var targetThemePack = restoreTheme ? (importedThemeId ?? settings.ThemePack) : (preview.HasTheme ? _preferences.Current.ThemePack : settings.ThemePack);
        settings = settings with { ThemePack = targetThemePack, PinOrder = settings.PinOrder ?? _preferences.Current.PinOrder };
        _isSaving = true;
        try
        {
            if (_preferences.Save(settings) is { } saveError)
            {
                AppLog.ReportError(saveError);
                return;
            }
        }
        finally
        {
            _isSaving = false;
        }
        var added = (Application.Current.MainWindow as MainWindow)?.ImportPins(pins) ?? 0;

        _applied = _preferences.Current;
        _initializing = true;
        try
        {
            LoadAllSelections(_applied);
        }
        finally
        {
            _initializing = false;
        }
        UpdateWidgetOptionState();
        MessageBox.Show(this, UiText.Format("FullBackupImportDone", added), "OpenTaskbar", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OpenLogFile_Click(object sender, RoutedEventArgs e) => AppLog.TryOpenLogFile();

    private void LoadAllSelections(DockSettings settings)
    {
        SelectChoice(ThemeBox, settings.Theme.ToString());
        SelectLanguage(settings.Language);
        SelectAlignment(settings);
        AnimationsToggle.IsChecked = settings.AnimationsEnabled;
        PreviewsToggle.IsChecked = settings.PreviewsEnabled;
        WindowPickerToggle.IsChecked = settings.ClickShowsWindowPicker;
        SelectChoice(AutoHideBox, settings.AutoHide.ToString());
        LoadSurfaceStyles(settings);
        MagnificationToggle.IsChecked = settings.MagnificationEnabled;
        SelectChoice(SizeBox, settings.Size.ToString());
        DockIconPaddingSlider.Value = settings.DockIconPadding;
        DockScaleSlider.Value = Math.Round(settings.DockScale * 100);
        SelectChoice(DockWidthModeBox, settings.DockWidthMode.ToString());
        UpdateDockWidthControls();
        SelectChoice(DockHeightModeBox, settings.DockHeightMode.ToString());
        UpdateDockHeightControls();
        LoadDockGapControls(settings);
        UpdateDockScaleLabel();
        var reloadProfile = DockMetrics.For(settings.Size, settings.DockScale);
        CornerRadiusSlider.Value = settings.DockCornerRadius ?? (int)Math.Round(reloadProfile.Radius);
        UpdateCornerRadiusLabel();
        LoadThemePacks(settings.ThemePack);
        foreach (var option in _widgetOptions)
        {
            option.IsInstalled = settings.InstalledWidgets?.Contains(option.Id, StringComparer.Ordinal) == true;
            option.IsEnabled = settings.Widgets?.Contains(option.Id, StringComparer.Ordinal) == true;
        }
        FocusMinutesSlider.Value = settings.FocusMinutes;
        BreakMinutesSlider.Value = settings.FocusBreakMinutes;
        FocusSessionsSlider.Value = settings.FocusSessions;
        LoadFeatureSelection(settings);
    }

    private void ResetDockDefaults_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new DockSettings();
        _gapBase = defaults;
        _initializing = true;
        try
        {
            SelectChoice(EdgeBox, defaults.Edge.ToString());
            SelectAlignment(defaults);
            SelectChoice(SizeBox, defaults.Size.ToString());
            DockIconPaddingSlider.Value = defaults.DockIconPadding;
            DockScaleSlider.Value = Math.Round(defaults.DockScale * 100);
            SelectChoice(DockWidthModeBox, defaults.DockWidthMode.ToString());
            UpdateDockWidthControls();
            SelectChoice(DockHeightModeBox, defaults.DockHeightMode.ToString());
            UpdateDockHeightControls();
            LoadDockGapControls(defaults);
            UpdateDockScaleLabel();
            SelectChoice(AutoHideBox, defaults.AutoHide.ToString());
            RevealOnWinKeyToggle.IsChecked = defaults.RevealDockOnWinKey;
            UpdateAutoHideControls();
            AnimationsToggle.IsChecked = defaults.AnimationsEnabled;
            MagnificationToggle.IsChecked = defaults.MagnificationEnabled;
            PreviewsToggle.IsChecked = defaults.PreviewsEnabled;
            WindowPickerToggle.IsChecked = defaults.ClickShowsWindowPicker;
            DynamicIslandEnabledToggle.IsChecked = defaults.DynamicIslandEnabled;
            SelectChoice(DynamicIslandVisibilityBox, defaults.DynamicIslandVisibility.ToString());
            DynamicIslandHideFullscreenToggle.IsChecked = defaults.DynamicIslandHideInFullscreen;
            DynamicIslandAutoHideToggle.IsChecked = defaults.DynamicIslandAutoHide;
            LoadDynamicIslandAppearance(defaults);
            LoadDynamicIslandPlacementChoices(defaults);
            UpdateDynamicIslandControls();
            RecycleBinToggle.IsChecked = defaults.ShowRecycleBin;
            CurrentDesktopToggle.IsChecked = defaults.CurrentDesktopOnly;
            SearchButtonToggle.IsChecked = defaults.ShowSearchButton;
            TaskViewButtonToggle.IsChecked = defaults.ShowTaskViewButton;
            TrayButtonToggle.IsChecked = defaults.ShowTrayButton;
            ShowDesktopButtonToggle.IsChecked = defaults.ShowDesktopButton;
            LoadDisplayChoices(defaults.TargetDisplay);
            AllMonitorsToggle.IsChecked = defaults.ShowOnAllMonitors;
            LoadSecondaryAppChoices(defaults.SecondaryApps);
            SecondaryWidgetsToggle.IsChecked = defaults.SecondaryWidgets;
            MirrorMainDockToggle.IsChecked = defaults.MirrorMainDock;
            UpdateMultiMonitorControls();
            _antigravityGroups = defaults.AntigravityGroups;
            UpdateAntigravityGroupCheckboxes();
        }
        finally
        {
            _initializing = false;
        }
        PreferenceEdited(sender, e);
        _gapBase = null;
    }

    private void UpdateWidgetOptionState()
    {
        bool IsExpanded(string id) => _widgetOptions.Any(o => string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase) && o.IsSettingsExpanded);
        ClockOptions.Visibility = IsExpanded("Clock") ? Visibility.Visible : Visibility.Collapsed;
        FocusOptions.Visibility = IsExpanded("Focus") ? Visibility.Visible : Visibility.Collapsed;
        StatsOptions.Visibility = IsExpanded("SystemStats") ? Visibility.Visible : Visibility.Collapsed;
        WeatherOptions.Visibility = IsExpanded("Weather") ? Visibility.Visible : Visibility.Collapsed;
        FocusMinutesValue.Text = UiText.Format("MinutesFormat", (int)Math.Round(FocusMinutesSlider.Value));
        BreakMinutesValue.Text = UiText.Format("MinutesFormat", (int)Math.Round(BreakMinutesSlider.Value));
        FocusSessionsValue.Text = ((int)Math.Round(FocusSessionsSlider.Value)).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private void FocusSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        PreferenceEdited(sender, e);
    }

    private void SaveSettings()
    {
        if (_closed) return;
        var startupChanged = _startupAvailable && StartupToggle.IsChecked != _startupEnabled;
        if (startupChanged && _startup.SetEnabled(StartupToggle.IsChecked == true) is { } startupError)
        {
            AppLog.Warning($"Startup error: {startupError}");
            return;
        }
        var settings = ReadSelection();
        _isInternalSave = true;
        try
        {
            if (_preferences.Save(settings) is { } error)
            {
                AppLog.Warning($"Save settings error: {error}");
                return;
            }
        }
        finally
        {
            _isInternalSave = false;
        }
        _applied = settings;
        _startupEnabled = StartupToggle.IsChecked == true;
    }

    private bool _isClosing;
    private bool _isClosingCompleted;

    private void FlushPendingAutoSaves()
    {
        CustomThemeEditor.FlushAutoSave();
        if (_autoSaveTimer.IsEnabled)
        {
            _autoSaveTimer.Stop();
            SaveSettings();
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        FlushPendingAutoSaves();
        if (_closingApplication || Application.Current.Dispatcher.HasShutdownStarted || _isClosingCompleted) return;
        if (_preferences.Current.EffectiveAnimationsEnabled && IsLoaded && !_isClosing)
        {
            e.Cancel = true;
            AnimateAndClose();
            return;
        }
    }

    private void AnimateAndClose()
    {
        if (_isClosing) return;
        _isClosing = true;

        var fade = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        };
        var scaleAnim = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.95, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        };

        var transform = new ScaleTransform(1.0, 1.0);
        SettingsFrame.CornerRadius = ActiveCornerRadius;
        SettingsFrame.RenderTransformOrigin = new Point(0.5, 0.5);
        SettingsFrame.RenderTransform = transform;

        fade.Completed += (_, _) =>
        {
            _isClosingCompleted = true;
            Close();
        };

        SettingsFrame.BeginAnimation(UIElement.OpacityProperty, fade);
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
    }

    private bool IsLivePreviewSource(object? source)
    {
        if (source is not DependencyObject element) return false;
        while (element is not null)
        {
            if (element is OpenTaskbar.Widgets.DockWidget widget && _livePreviewWidgets.Contains(widget))
                return true;
            element = VisualTreeHelper.GetParent(element) as DependencyObject
                ?? LogicalTreeHelper.GetParent(element);
        }
        return false;
    }

    private void PreferenceEdited(object sender, RoutedEventArgs e)
    {
        if (_initializing || _closed) return;
        // Live preview widgets are real widget instances; their internal ToggleButtons (Wi‑Fi,
        // Bluetooth, Night Light, etc.) bubble Checked/Unchecked to this window-wide handler.
        // Those are display state, not user edits — ignore them so they never trigger a save.
        if (_livePreviewsAttached && IsLivePreviewSource(e.OriginalSource)) return;
        if (e.OriginalSource is ToggleButton { TemplatedParent: ComboBox }) return;
        // Unnamed switches inside a widget card are the install toggles; their Click handler
        // saves explicitly. Named switches live in the per-widget settings panels and auto-save.
        if (e.OriginalSource is CheckBox { DataContext: DockWidgetOption, Name: "" }) return;
        var selectedSize = Enum.TryParse<DockSize>(Choice(SizeBox), out var size) ? size : DockSize.Compact;
        (Application.Current.MainWindow as MainWindow)?.SetSizePreview(
            selectedSize, Math.Round(DockScaleSlider.Value) / 100,
            (int)Math.Round(DockIconPaddingSlider.Value));
        UpdateWidgetOptionState();
        UpdateMultiMonitorControls();
        // Reflect widget-related edits in the catalog immediately, without waiting for the
        // debounced save. Every live preview is a real widget, so ApplySettings re-renders it.
        ApplySettingsToLivePreviews(ReadSelection());
        TriggerAutoSave(immediate: sender is not (Slider or TextBox));
    }

    private void TriggerAutoSave(bool immediate)
    {
        if (_initializing || _closed) return;
        if (immediate)
        {
            _autoSaveTimer.Stop();
            SaveSettings();
        }
        else
        {
            _autoSaveTimer.Stop();
            _autoSaveTimer.Start();
        }
    }

    private void RegisterSurface(SurfaceKind kind, Slider slider, TextBlock value, ComboBox box)
    {
        _surfaceSliders[kind] = slider;
        _surfaceValues[kind] = value;
        _surfaceBoxes[kind] = box;
    }

    private static SurfaceKind SurfaceOf(object sender) =>
        (sender as FrameworkElement)?.Tag is string tag && Enum.TryParse(tag, out SurfaceKind kind) ? kind : SurfaceKind.Dock;

    private SurfaceBackground SurfaceBackgroundOf(SurfaceKind kind) =>
        Enum.TryParse<SurfaceBackground>(Choice(_surfaceBoxes[kind]), out var background) ? background : SurfaceBackground.None;

    private void LoadSurfaceStyles(DockSettings settings)
    {
        var previous = _initializing;
        _initializing = true;
        try
        {
            LoadSurface(SurfaceKind.Dock, settings.DockBackground, settings.DockOpacity);
            LoadSurface(SurfaceKind.Menu, settings.MenuBackground, settings.MenuOpacity);
            LoadSurface(SurfaceKind.Widget, settings.WidgetBackground, settings.WidgetOpacity);
            LoadSurface(SurfaceKind.Settings, settings.SettingsBackground, settings.SettingsOpacity);
            LoadBorderStyle(settings);
        }
        finally
        {
            _initializing = previous;
        }
    }

    private void LoadSurface(SurfaceKind kind, SurfaceBackground background, double opacity)
    {
        SelectChoice(_surfaceBoxes[kind], background.ToString());
        _surfaceSliders[kind].Value = Math.Clamp(opacity * 100, 0, 100);
        _surfaceSliders[kind].IsEnabled = background != SurfaceBackground.None;
        _surfaceValues[kind].Text = UiText.Format("OpacityPercent", (int)Math.Round(_surfaceSliders[kind].Value));
    }

    private void SegmentChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        if (ReferenceEquals(sender, EdgeBox))
        {
            UpdateDockWidthControls();
            UpdateDockHeightControls();
        }
        else if (ReferenceEquals(sender, AutoHideBox))
        {
            UpdateAutoHideControls();
        }
        PreferenceEdited(sender, e);
    }

    private void SurfaceStyle_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        var kind = SurfaceOf(sender);
        var background = SurfaceBackgroundOf(kind);
        _surfaceSliders[kind].IsEnabled = background != SurfaceBackground.None;
        _hasUnappliedThemePreview = true;
        ThemeManager.SetSurface(kind, background, Math.Round(_surfaceSliders[kind].Value) / 100);
        if (kind == SurfaceKind.Settings) UpdateBackdrop();
        PreferenceEdited(sender, e);
    }

    private void SurfaceOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        var kind = SurfaceOf(sender);
        _surfaceValues[kind].Text = UiText.Format("OpacityPercent", (int)Math.Round(e.NewValue));
        _hasUnappliedThemePreview = true;
        ThemeManager.SetSurface(kind, SurfaceBackgroundOf(kind), Math.Round(e.NewValue) / 100);
        PreferenceEdited(sender, e);
    }

    // Global border style; applied to all four surfaces so menus, widgets, settings and the dock share one look.
    private string? _borderColor;

    private void LoadBorderStyle(DockSettings settings)
    {
        var thickness = Math.Clamp(double.IsFinite(settings.MenuBorderThickness) ? settings.MenuBorderThickness : 1.0, 0, 6);
        _borderColor = string.IsNullOrWhiteSpace(settings.MenuBorderColor) ? null : settings.MenuBorderColor;
        BorderThicknessSlider.Value = thickness;
        UpdateBorderLabels();
    }

    private void UpdateBorderLabels()
    {
        if (BorderThicknessValue is null) return;
        BorderThicknessValue.Text = BorderThicknessSlider.Value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        if (BorderColorSwatch is null) return;
        if (_borderColor is not null && ThemeManager.TryParseColor(_borderColor, out var color))
            BorderColorSwatch.Background = new System.Windows.Media.SolidColorBrush(color);
        else
            BorderColorSwatch.SetResourceReference(Border.BackgroundProperty, "MenuStrokeMaterial");
    }

    private void ApplyBorderToAll(double thickness, string? color)
    {
        _hasUnappliedThemePreview = true;
        foreach (var kind in Enum.GetValues<SurfaceKind>()) ThemeManager.SetBorder(kind, thickness, color);
    }

    private void BorderThicknessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateBorderLabels();
        if (_initializing) return;
        ApplyBorderToAll(Math.Round(e.NewValue * 2) / 2, _borderColor);
        PreferenceEdited(sender, e);
    }

    private void BorderColorButton_Click(object sender, RoutedEventArgs e)
    {
        var source = BorderColorSwatch.Background as System.Windows.Media.Brush
            ?? TryFindResource("MenuStrokeMaterial") as System.Windows.Media.Brush
            ?? System.Windows.Media.Brushes.Transparent;
        var picker = new ThemeBrushPicker(source) { Owner = this };
        if (picker.ShowDialog() == true && picker.Result is System.Windows.Media.Brush brush)
        {
            _borderColor = ThemeBrushPicker.BrushToHex(brush);
            UpdateBorderLabels();
            ApplyBorderToAll(Math.Round(BorderThicknessSlider.Value * 2) / 2, _borderColor);
            PreferenceEdited(sender, e);
        }
    }

    private void DockWidthModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateDockWidthControls();
        if (!_initializing) PreferenceEdited(sender, e);
    }

    private void DockWidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateDockWidthLabel();
        if (!_initializing) PreferenceEdited(sender, e);
    }

    private static DockGapUnit GapUnit(ComboBox box) =>
        Enum.TryParse<DockGapUnit>(Choice(box), out var unit) ? unit : DockGapUnit.Pixels;

    private void LoadDockGapControls(DockSettings settings)
    {
        // Caller controls _initializing (constructor / reset both set it). Do not toggle it here: setting it to
        // false early would let slider/checkbox events fire and mark preferences as edited during load.
        SelectChoice(DockTopGapUnitBox, settings.DockTopGapUnit.ToString());
        ConfigureGapSlider(DockTopGapSlider, settings.DockTopGapUnit, settings.DockTopGapPixels, settings.DockTopGapPercent);
        SelectChoice(DockBottomGapUnitBox, settings.DockBottomGapUnit.ToString());
        ConfigureGapSlider(DockBottomGapSlider, settings.DockBottomGapUnit, settings.DockBottomGapPixels, settings.DockBottomGapPercent);
        UpdateDockGapLabels();
    }

    private static void ConfigureGapSlider(Slider slider, DockGapUnit unit, int pixels, int percent)
    {
        slider.Minimum = 0;
        slider.Maximum = unit == DockGapUnit.Percent ? 100 : 200;
        slider.TickFrequency = 1;
        slider.Value = unit == DockGapUnit.Percent
            ? Math.Clamp(percent, 0, 100)
            : Math.Clamp(pixels, 0, 200);
    }

    private void UpdateDockGapLabels()
    {
        // XAML sets slider Values during InitializeComponent, before the unit ComboBoxes exist.
        if (DockTopGapValue is null || DockBottomGapValue is null) return;
        DockTopGapValue.Text = Math.Round(DockTopGapSlider.Value) + (DockTopGapUnitBox is not null && GapUnit(DockTopGapUnitBox) == DockGapUnit.Percent ? "%" : "px");
        DockBottomGapValue.Text = Math.Round(DockBottomGapSlider.Value) + (DockBottomGapUnitBox is not null && GapUnit(DockBottomGapUnitBox) == DockGapUnit.Percent ? "%" : "px");
    }

    private void DockTopGapUnitBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        var unit = GapUnit(DockTopGapUnitBox);
        var current = _preferences.Current;
        var wasInitializing = _initializing;
        _initializing = true;
        try
        {
            ConfigureGapSlider(DockTopGapSlider, unit, current.DockTopGapPixels, current.DockTopGapPercent);
        }
        finally
        {
            _initializing = wasInitializing;
        }
        UpdateDockGapLabels();
        PreferenceEdited(sender, e);
    }

    private void DockBottomGapUnitBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        var unit = GapUnit(DockBottomGapUnitBox);
        var current = _preferences.Current;
        var wasInitializing = _initializing;
        _initializing = true;
        try
        {
            ConfigureGapSlider(DockBottomGapSlider, unit, current.DockBottomGapPixels, current.DockBottomGapPercent);
        }
        finally
        {
            _initializing = wasInitializing;
        }
        UpdateDockGapLabels();
        PreferenceEdited(sender, e);
    }

    private void DockTopGapSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateDockGapLabels();
        if (!_initializing) PreferenceEdited(sender, e);
    }

    private void DockBottomGapSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateDockGapLabels();
        if (!_initializing) PreferenceEdited(sender, e);
    }

    private void UpdateDockWidthControls()
    {
        var vertical = Choice(EdgeBox) is nameof(DockEdge.Left) or nameof(DockEdge.Right);
        if (DockWidthModeLabel is not null)
        {
            DockWidthModeLabel.Text = UiText.Get(vertical ? "DockHeightMode" : "DockWidthMode");
            DockWidthModeDescription.Text = UiText.Get(vertical ? "DockHeightModeDescription" : "DockWidthModeDescription");
            DockWidthValueLabel.Text = UiText.Get(vertical ? "DockHeightValue" : "DockWidthValue");
            DockWidthValueDescription.Text = UiText.Get(vertical ? "DockHeightValueDescription" : "DockWidthValueDescription");
        }

        var mode = Choice(DockWidthModeBox) ?? nameof(DockWidthMode.Auto);
        var fixedMode = mode is nameof(DockWidthMode.Pixels) or nameof(DockWidthMode.Percent);
        DockWidthValueRow.Visibility = fixedMode ? Visibility.Visible : Visibility.Collapsed;
        if (fixedMode)
        {
            var settings = _preferences.Current;
            var isPercent = mode == nameof(DockWidthMode.Percent);
            var value = isPercent ? settings.DockWidthPercent : settings.DockWidthPixels;
            DockWidthSlider.Minimum = isPercent ? 10 : 100;
            DockWidthSlider.Maximum = isPercent ? 100 : (vertical ? 3000 : 5000);
            // A 50-step tick over the 10–100 percent range only exposes 10, 60 and 100. Snap to whole percents
            // instead so the user can pick any percentage; pixels keep the coarse 50 px cadence.
            DockWidthSlider.TickFrequency = isPercent ? 1 : 50;
            DockWidthSlider.IsSnapToTickEnabled = true;
            DockWidthSlider.Value = Math.Clamp(value, DockWidthSlider.Minimum, DockWidthSlider.Maximum);
            UpdateDockWidthLabel();
        }
    }

    private void UpdateDockWidthLabel()
    {
        if (DockWidthValue is null || DockWidthSlider is null) return;
        var suffix = (Choice(DockWidthModeBox) ?? nameof(DockWidthMode.Auto)) == nameof(DockWidthMode.Percent) ? "%" : "px";
        DockWidthValue.Text = Math.Round(DockWidthSlider.Value) + suffix;
    }

    private void DockHeightModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateDockHeightControls();
        if (!_initializing) PreferenceEdited(sender, e);
    }

    private void DockHeightSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateDockHeightLabel();
        if (!_initializing) PreferenceEdited(sender, e);
    }

    private void UpdateDockHeightControls()
    {
        if (DockHeightModeBox is null || DockHeightModeRow is null) return;
        // Side docks take their thickness from the width setting; the height setting is horizontal-only.
        var vertical = Choice(EdgeBox) is nameof(DockEdge.Left) or nameof(DockEdge.Right);
        DockHeightModeRow.Visibility = vertical ? Visibility.Collapsed : Visibility.Visible;

        var mode = Choice(DockHeightModeBox) ?? nameof(DockHeightMode.Auto);
        var fixedMode = mode is nameof(DockHeightMode.Pixels) or nameof(DockHeightMode.Percent);
        DockHeightValueRow.Visibility = fixedMode && !vertical ? Visibility.Visible : Visibility.Collapsed;
        if (fixedMode)
        {
            var settings = _preferences.Current;
            var isPercent = mode == nameof(DockHeightMode.Percent);
            DockHeightSlider.Minimum = isPercent ? 2 : 24;
            DockHeightSlider.Maximum = isPercent ? 25 : 400;
            DockHeightSlider.TickFrequency = isPercent ? 1 : 4;
            DockHeightSlider.IsSnapToTickEnabled = true;
            DockHeightSlider.Value = Math.Clamp(isPercent ? settings.DockHeightPercent : settings.DockHeightPixels,
                DockHeightSlider.Minimum, DockHeightSlider.Maximum);
            UpdateDockHeightLabel();
        }
    }

    private void UpdateDockHeightLabel()
    {
        if (DockHeightValue is null || DockHeightSlider is null) return;
        var suffix = (Choice(DockHeightModeBox) ?? nameof(DockHeightMode.Auto)) == nameof(DockHeightMode.Percent) ? "%" : "px";
        DockHeightValue.Text = Math.Round(DockHeightSlider.Value) + suffix;
    }

    private void UpdateDockScaleLabel() => DockScaleValue.Text = UiText.Format("OpacityPercent", (int)Math.Round(DockScaleSlider.Value));

    private void UpdateCornerRadiusLabel() => CornerRadiusValue.Text = $"{(int)Math.Round(CornerRadiusSlider.Value)} px";

    private void CornerRadiusSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        UpdateCornerRadiusLabel();
        (Application.Current.MainWindow as MainWindow)?.SetGeometryPreview((int)Math.Round(CornerRadiusSlider.Value));
        PreferenceEdited(sender, e);
    }

    private void DockIconPaddingSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        UpdateDockIconPaddingLabel();
        PreferenceEdited(sender, e);
    }

    private void UpdateDockIconPaddingLabel() =>
        DockIconPaddingValue.Text = $"{(int)Math.Round(DockIconPaddingSlider.Value)} px";

    // Padding affects layout only (no work-area reservation), so the debounced auto-save is enough:
    // PreferenceEdited previews it live and SaveSettings persists + reapplies it through Preferences.Changed.

    private void DockScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        UpdateDockScaleLabel();
        var selectedSize = Enum.TryParse<DockSize>(Choice(SizeBox), out var size) ? size : DockSize.Compact;
        (Application.Current.MainWindow as MainWindow)?.SetSizePreview(
            selectedSize, Math.Round(DockScaleSlider.Value) / 100,
            (int)Math.Round(DockIconPaddingSlider.Value));
        // While dragging with the mouse button held, defer saving until PreviewMouseUp (CommitDockScale) so the
        // replacement session is not restarted repeatedly mid-drag; still auto-save wheel/programmatic changes.
        if (Mouse.LeftButton != MouseButtonState.Pressed)
        {
            TriggerAutoSave(immediate: false);
        }
    }

    // Applying the scale commits the work-area reservation, which needs a fresh replacement session. Doing that on
    // every tick of a drag would be expensive, so commit once when the user lets go of the slider instead.
    private void DockScaleSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e) => CommitDockScale();

    private void DockScaleSlider_KeyUp(object sender, KeyEventArgs e) => CommitDockScale();

    private void CommitDockScale()
    {
        if (_initializing || _closed) return;
        _autoSaveTimer.Stop();
        SaveSettings();
        (Application.Current.MainWindow as MainWindow)?.SetSizePreview(null, null);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    internal void CloseForShutdown()
    {
        // Window_Closing returns immediately during application shutdown, so flush the debounced edits here.
        if (_autoSaveTimer.IsEnabled)
        {
            _autoSaveTimer.Stop();
            SaveSettings();
        }
        _closingApplication = true;
        Close();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_recordingHotkeyTarget != HotkeyTarget.None)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            var modifiers = Keyboard.Modifiers;
            if (key == Key.Escape)
            {
                e.Handled = true;
                CancelHotkeyRecording();
                return;
            }

            if (modifiers == ModifierKeys.None && key is Key.Back or Key.Delete)
            {
                e.Handled = true;
                if (_recordingHotkeyTarget == HotkeyTarget.Dock)
                {
                    _dockCustomHotkey = "Disabled";
                    _dockHotkeyChoice = HotkeyChoice.Disabled;
                }
                else
                {
                    _launcherCustomHotkey = "Disabled";
                    _launcherHotkeyChoice = HotkeyChoice.Disabled;
                }
                _recordingHotkeyTarget = HotkeyTarget.None;
                ClearHotkeyValidation();
                UpdateHotkeyControls();
                TriggerAutoSave(immediate: true);
                return;
            }

            if (!HotkeyBinding.TryFromKeyEvent(modifiers, key, out var binding, out var errorKey))
            {
                if (errorKey is not null)
                {
                    e.Handled = true;
                    ShowHotkeyValidation(UiText.Get(errorKey));
                }
                return;
            }

            e.Handled = true;
            var otherBinding = _recordingHotkeyTarget == HotkeyTarget.Dock
                ? HotkeyBinding.Resolve(_launcherHotkeyChoice, _launcherCustomHotkey)
                : HotkeyBinding.Resolve(_dockHotkeyChoice, _dockCustomHotkey);
            if (HotkeyBinding.EqualsBinding(binding, otherBinding))
            {
                var otherNameKey = _recordingHotkeyTarget == HotkeyTarget.Dock ? "LauncherHotkey" : "DockHotkey";
                _recordingHotkeyTarget = HotkeyTarget.None;
                UpdateHotkeyControls();
                ShowHotkeyValidation(UiText.Format("HotkeyConflict", UiText.Get(otherNameKey)));
                return;
            }

            if (_recordingHotkeyTarget == HotkeyTarget.Dock)
                _dockCustomHotkey = binding.DisplayText;
            else
                _launcherCustomHotkey = binding.DisplayText;
            _recordingHotkeyTarget = HotkeyTarget.None;
            ClearHotkeyValidation();
            UpdateHotkeyControls();
            TriggerAutoSave(immediate: true);
            return;
        }

        if (e.Key != Key.Escape) return;
        if (!string.IsNullOrEmpty(SettingsSearchBox.Text)
            && (SettingsSearchBox.IsKeyboardFocusWithin || SettingsSearchOverlay.Visibility == Visibility.Visible))
        {
            SettingsSearchBox.Text = string.Empty;
            e.Handled = true;
            return;
        }
        e.Handled = true;
        Close();
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint(int x, int y) { public int X = x; public int Y = y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
}
