using System.Globalization;
using System.Text.Json.Serialization;
using OpenTaskbar.Services;

namespace OpenTaskbar.Models;

public enum DockPosition
{
    Left,
    Center,
    Right
}

/// <summary>How dock contents (apps + widgets) are aligned inside a full-width dock bar.</summary>
public enum DockContentAlignment
{
    Left,
    Center,
    Right
}

public enum AppTheme { Dark, Light, System }
public readonly record struct AppLanguage
{
    private readonly string? _cultureName;
    public AppLanguage(string cultureName)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);
        _cultureName = string.Equals(culture.Name, "en-US", StringComparison.OrdinalIgnoreCase) ? null : culture.Name;
    }
    public string CultureName => _cultureName ?? "en-US";
    public static AppLanguage Turkish { get; } = new("tr-TR");
    public static AppLanguage English => default;
    public static AppLanguage Japanese { get; } = new("ja-JP");
    public CultureInfo Culture => CultureInfo.GetCultureInfo(CultureName);
    public string NativeName => Culture.NativeName;
    public string DisplayName => Culture.TextInfo.ToTitleCase(NativeName);
    public string Code => Culture.TwoLetterISOLanguageName.ToUpperInvariant();

    /// <summary>Embedded country flag image for this culture, or null when no flag is bundled.</summary>
    public System.Windows.Media.ImageSource? FlagImage => Services.CountryFlag.Load(this);
}
public enum AutoHideMode { Disabled, Smart, Always }
public enum DockLayout { Floating, FullWidth }
public enum DockSize { Compact, Medium, Large }
public enum DockEdge { Bottom, Left, Right }
public enum HotkeyChoice { Disabled, AltSpace, CtrlSpace, CtrlAltSpace, CtrlAltD, WinAltD }
public enum StartButtonAction { WindowsStart, Launcher }
public enum StartButtonPlacement { Start, End, Hidden, InWidgets }
public enum ClockDateStyle { DayAndShortDate, ShortDateOnly, NumericDate, Hidden }

/// <summary>Explicit numeric date layouts available on the date &amp; time widget. System follows the UI culture.</summary>
public enum ClockDateFormat { System, DdMmYyyy, DdMmYy, MmDdYyyy, YyyyMmDd, DdMmYyyyDots }

/// <summary>Places the clock date beside the time or stacked below it to save horizontal dock space.</summary>
public enum ClockDateLayout { Beside, Below }

/// <summary>Temperature scale used by the weather widget. Values stay in Celsius internally and convert when rendered.</summary>
public enum TemperatureUnit { Celsius, Fahrenheit }

public enum SecondaryAppScope { CurrentMonitorOnly, AllMonitors }
public enum WidgetDensityMode { Auto, Compact, Full }
public enum DynamicIslandVisibilityMode { Always, DesktopOnly }
/// <summary>How the dock's horizontal width is determined. Auto fits content; Fixed/Pin modes pin it; Fill spans edge-to-edge.</summary>
public enum DockWidthMode { Auto, Pixels, Percent, Fill }

/// <summary>How the horizontal dock's thickness is determined. Auto follows the size profile; Fill makes the visible bar cover the whole reserved strip.</summary>
public enum DockHeightMode { Auto, Pixels, Percent, Fill }

/// <summary>Unit used for the dock's external top/bottom spacing.</summary>
public enum DockGapUnit { Pixels, Percent }

/// <summary>How a translucent UI surface is filled. None removes the fill, its rim and its shadow.</summary>
public enum SurfaceBackground { Glass, Solid, None }

/// <summary>The independently configurable parts of the application.</summary>
public enum SurfaceKind { Dock, Menu, Widget, Settings }

/// <summary>Per-surface material style. BorderColor is an optional "#AARRGGBB" override; null keeps the theme source.</summary>
public readonly record struct SurfaceStyle(
    SurfaceBackground Background,
    double Opacity,
    double BorderThickness = 1.0,
    string? BorderColor = null);

/// <summary>The individual meters of the System meters widget; a cleared flag hides that meter.</summary>
[Flags]
public enum SystemMeters
{
    None = 0,
    Cpu = 1,
    Memory = 2,
    Battery = 4,
    // 8 was CpuTemperature; kept unused so persisted numeric values stay stable.
    Gpu = 16,
    GpuTemperature = 32,
    All = Cpu | Memory | Gpu | GpuTemperature
}

[Flags]
public enum AiProviders { None = 0, Claude = 1, Codex = 2, Antigravity = 4, OpenCode = 8, Copilot = 16, DeepSeek = 32, OpenRouter = 64, Cursor = 128, All = Claude | Codex | Antigravity | OpenCode | Copilot | DeepSeek | OpenRouter | Cursor }

/// <summary>Which quota window the AI usage ring shows; Highest follows whichever window is closest to its limit.</summary>
public enum AiUsageRing { Highest, Session, Weekly }

/// <summary>Which Antigravity model groups are displayed on the dock widget card.</summary>
[Flags]
public enum AntigravityGroups { None = 0, Gemini = 1, Claude = 2, Argon = 4, All = Gemini | Claude | Argon }

/// <summary>A named dock configuration snapshot (widgets, layout, appearance, hotkeys, and hidden pinned apps).</summary>
public sealed record DockProfile(
    string Name,
    IReadOnlyList<string> HiddenApps,
    IReadOnlyList<string>? Widgets = null,
    IReadOnlyList<string>? InstalledWidgets = null,
    IReadOnlyList<string>? WidgetOrder = null,
    IReadOnlyList<string>? DockItemOrder = null,
    DockWidthMode? DockWidthMode = null,
    int? DockWidthPixels = null,
    int? DockWidthPercent = null,
    AppTheme? Theme = null,
    string? ThemePack = null,
    bool? ThemeApplicationIcons = null,
    HotkeyChoice? LauncherHotkey = null,
    HotkeyChoice? DockHotkey = null,
    AutoHideMode? AutoHide = null,
    string? CustomLauncherHotkey = null,
    string? CustomDockHotkey = null);

/// <summary>A user-defined group of applications shown as a single dock icon with a flyout.</summary>
public sealed record DockAppGroup(string Id, string Name, IReadOnlyList<string> ExecutablePaths);

// Missing JSON properties fall back to these constructor defaults, so older settings files stay valid.
public sealed record DockSettings(
    DockPosition Position = DockPosition.Center,
    IReadOnlyList<string>? PinOrder = null,
    AppTheme Theme = AppTheme.System,
    AppLanguage Language = default,
    bool AnimationsEnabled = true,
    bool PreviewsEnabled = true,
    bool ClickShowsWindowPicker = false,
    bool NotificationBadgesEnabled = true,
    AutoHideMode AutoHide = AutoHideMode.Disabled,
    // Legacy: migrated into the four per-surface styles on load, no longer edited by the UI.
    double SurfaceOpacity = 0.9,
    SurfaceBackground DockBackground = SurfaceBackground.Glass,
    double DockOpacity = 0.9,
    double DockBorderThickness = 1.0,
    string? DockBorderColor = null,
    SurfaceBackground MenuBackground = SurfaceBackground.Glass,
    double MenuOpacity = 0.9,
    double MenuBorderThickness = 1.0,
    string? MenuBorderColor = null,
    SurfaceBackground WidgetBackground = SurfaceBackground.Glass,
    double WidgetOpacity = 0.9,
    double WidgetBorderThickness = 1.0,
    string? WidgetBorderColor = null,
    SurfaceBackground SettingsBackground = SurfaceBackground.Glass,
    double SettingsOpacity = 0.9,
    double SettingsBorderThickness = 1.0,
    string? SettingsBorderColor = null,
    IReadOnlyList<string>? Widgets = null,
    IReadOnlyList<string>? InstalledWidgets = null,
    DockLayout Layout = DockLayout.Floating,
    DockContentAlignment ContentAlignment = DockContentAlignment.Center,
    DockSize Size = DockSize.Compact,
    bool MagnificationEnabled = true,
    bool ShowClock = false,
    bool ShowMedia = false,
    bool ShowSystemStats = false,
    bool ShowFocus = false,
    bool ShowAudio = false,
    bool ShowQuickSettings = false,
    int FocusMinutes = 25,
    int FocusBreakMinutes = 5,
    int FocusSessions = 4,
    bool ShowRecycleBin = true,
    HotkeyChoice LauncherHotkey = HotkeyChoice.AltSpace,
    HotkeyChoice DockHotkey = HotkeyChoice.CtrlAltD,
    StartButtonAction StartAction = StartButtonAction.WindowsStart,
    StartButtonPlacement StartPlacement = StartButtonPlacement.Start,
    int StartWidgetIndex = 0,
    // Wheel-driven volume is off by default. Renamed from WheelVolume so legacy settings.json files
    // carrying "WheelVolume": true are ignored and existing users get the new off default too.
    bool WheelVolumeEnabled = false,
    bool CheckUpdates = true,
    bool ShowWeather = false,
    string WeatherCity = "",
    bool CurrentDesktopOnly = false,
    bool ShowOnAllMonitors = true,
    string? TargetDisplay = null,
    SecondaryAppScope SecondaryApps = SecondaryAppScope.CurrentMonitorOnly,
    bool SecondaryWidgets = true,
    DockEdge Edge = DockEdge.Bottom,
    // Changed from the dock itself, not the Settings edit buffer. Null stacks means "Downloads only".
    IReadOnlyList<string>? StackFolders = null,
    IReadOnlyList<DockProfile>? Profiles = null,
    string? ActiveProfile = null,
    double DockScale = 1.0,
    IReadOnlyList<string>? WidgetOrder = null,
    bool Clock24Hour = true,
    bool ClockShowSeconds = false,
    ClockDateStyle DateStyle = ClockDateStyle.DayAndShortDate,
    SystemMeters StatsMeters = SystemMeters.All,
    // Folder name under %LOCALAPPDATA%\OpenTaskbar\Themes; null keeps the built-in look.
    string? ThemePack = null,
    bool ThemeApplicationIcons = true,
    WidgetDensityMode GlobalWidgetDensity = WidgetDensityMode.Auto,
    IReadOnlyDictionary<string, WidgetDensityMode>? WidgetDensityOverrides = null,
    // Free-form dock order: "start", "search", "taskview", "tray", "peek", "app:<path>", "stack:<path>", "widget:<id>", "bin".
    // Null keeps the legacy grouped layout until the user drags an item.
    IReadOnlyList<string>? DockItemOrder = null,
    // Custom taskbar corner radius override (null = auto from size profile).
    int? DockCornerRadius = null,
    // Legacy: read only to migrate the retired single AI usage widget; every provider is now its own widget.
    AiProviders AiUsageProviders = AiProviders.None,
    AiUsageRing AiUsageRing = AiUsageRing.Highest,
    AntigravityGroups AntigravityGroups = AntigravityGroups.All,
    // User-defined application groups. Each app belongs to at most one group.
    IReadOnlyList<DockAppGroup>? AppGroups = null,
    // Shows the Search button beside Start. Search used to be a widget; the setting keeps the same opt-out.
    bool ShowSearchButton = true,
    // Permanent shell buttons that used to be (or would have been) widgets.
    bool ShowTaskViewButton = true,
    bool ShowTrayButton = true,
    bool ShowDesktopButton = true,
    // Optional fixed dock width. Auto keeps the current content-fitted behaviour;
    // Pixels pins an exact DIP width; Percent pins a share of the target monitor.
    DockWidthMode DockWidthMode = DockWidthMode.Auto,
    int DockWidthPixels = 900,
    int DockWidthPercent = 60,
    // External spacing around the dock surface (outside the visible bar, not inner padding). Each side can be
    // zero to sit flush against the screen edge, and can be expressed in pixels or a share of the screen.
    DockGapUnit DockTopGapUnit = DockGapUnit.Pixels,
    int DockTopGapPixels = 0,
    int DockTopGapPercent = 0,
    DockGapUnit DockBottomGapUnit = DockGapUnit.Pixels,
    int DockBottomGapPixels = 6,
    int DockBottomGapPercent = 0,
    // Extra horizontal gap (DIP, left+right) between dock icons, independent of size profile and DockScale.
    // Does not affect icon size or dock height. Zero keeps the original layout.
    int DockIconPadding = 0,
    bool FocusModeEnabled = false,
    string? CustomLauncherHotkey = null,
    string? CustomDockHotkey = null,
    // Stable tray icon identifiers (from TrayPinIdentity) shown directly beside the tray chevron while running.
    IReadOnlyList<string>? PinnedTrayIcons = null,
    // Thickness of the horizontal dock bar. Auto keeps the size profile's natural height; Pixels/Percent pin the
    // visible bar height; Fill stretches it across the whole reserved strip (bottom spacing no longer applies).
    // Appended at the end so older settings files and positional constructor calls keep working.
    DockHeightMode DockHeightMode = DockHeightMode.Auto,
    int DockHeightPixels = 48,
    int DockHeightPercent = 7,
    int WirelessBatteryPollSeconds = 30,
    int WirelessBatteryLowThreshold = 0,
    IReadOnlyList<string>? IgnoredWirelessDevices = null,
    IReadOnlyDictionary<string, string>? WirelessDeviceAliases = null,
    bool WirelessBatteryListenOnly = false,
    // Appended at the end so older settings files and positional constructor calls keep working.
    TemperatureUnit WeatherUnit = TemperatureUnit.Celsius,
    ClockDateFormat DateFormat = ClockDateFormat.System,
    // Widgets assigned to a Dynamic Island are keyed by widget ID and display device name. "*" targets all displays.
    // Appended so existing settings JSON and positional constructor calls remain compatible.
    IReadOnlyDictionary<string, string>? DynamicIslandPlacements = null,
    bool RevealDockOnWinKey = true,
    ClockDateLayout ClockDateLayout = ClockDateLayout.Beside,
    bool MirrorMainDock = false,
    DynamicIslandVisibilityMode DynamicIslandVisibility = DynamicIslandVisibilityMode.Always,
    bool DynamicIslandHideInFullscreen = true,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? DynamicIslandWidgetOrders = null,
    bool DynamicIslandEnabled = false,
    string? DynamicIslandBackgroundColor = null,
    double DynamicIslandOpacity = 1.0,
    bool DynamicIslandAutoHide = false)
{
    public const string DynamicIslandAllDisplays = "*";
    /// <summary>Effective animation state, suppressed while focus mode is active.</summary>
    [JsonIgnore]
    public bool EffectiveAnimationsEnabled => AnimationsEnabled && !FocusModeEnabled;

    /// <summary>Effective notification badge state, suppressed while focus mode is active.</summary>
    [JsonIgnore]
    public bool EffectiveNotificationBadgesEnabled => NotificationBadgesEnabled && !FocusModeEnabled;

    /// <summary>
    /// Parsed view of <see cref="PinnedTrayIcons"/>. Legacy <c>guid:</c>/<c>path:</c> strings and structured JSON
    /// entries are both understood; unparsable entries are skipped so one bad pin cannot break the settings UI.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<TrayIconRef> PinnedTrayIconRefs => PinnedTrayIcons is null
        ? []
        : PinnedTrayIcons
            .Select(key => TrayPinIdentity.TryParseRef(key, out var reference) && reference.IsPinnable
                ? reference
                : (TrayIconRef?)null)
            .Where(reference => reference.HasValue)
            .Select(reference => reference!.Value)
            .ToArray();
}
