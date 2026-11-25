using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using OpenTaskbar;
using OpenTaskbar.Models;
using OpenTaskbar.Services;
using OpenTaskbar.Views;
using OpenTaskbar.Widgets;

internal static class SettingsAndAutoHideChecks
{
    public static int Run(Application application)
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        CheckSettingsSurviveReopen(application, Check);
        CheckGapUnitsWeatherAndThemeEditor(application, Check);
        CheckLegacySettingsMigration(Check);
        CheckAutoHideLifecycleAndPointerSync(application, Check);
        CheckFlyoutState(application, Check);
        CheckClockWidgetFlyoutAndAutoHide(application, Check);
        CheckTrayTransientTooltips(Check);
        CheckForegroundLocationForceRepublish(application, Check);
        CheckSettingsWindowCornerAndBorder(application, Check);
        CheckSettingsWindowPreviewDensityAndAntigravityGroups(application, Check);
        CheckStartupRegistrationSyncAndParsing(Check);
        return checks;
    }

    private static void CheckForegroundLocationForceRepublish(Application application, Action<bool, string> check)
    {
        // Issue #85: Smart auto-hide relies on ForegroundWindowChanged re-firing when the SAME foreground window
        // moves, resizes, or restores, while normal refresh deduping must still suppress duplicate publishes.
        using var tracker = new NativeWindowTracker(application.Dispatcher);
        var published = new List<nint>();
        tracker.ForegroundWindowChanged += h => published.Add(h);
        const nint fakeWindow = 0x12345;
        tracker.PublishForeground(fakeWindow);
        tracker.PublishForeground(fakeWindow);
        check(published.Count == 1,
            $"Duplicate foreground publish without force must be deduped (got {published.Count}).");
        tracker.PublishForeground(fakeWindow, force: true);
        check(published.Count == 2 && published[1] == fakeWindow,
            $"Location-change publish with force=true must re-emit ForegroundWindowChanged for smart auto-hide (#85, got {published.Count}).");

        // Issue #86: StopForRestart followed by Dispose must mark the session as stopped for restart so Dispose
        // skips Stop()/RestoreWithVerification and never flashes the native Windows taskbar.
        var dummyWindow = new Window { ShowInTaskbar = false, ShowActivated = false };
        using (var restartSession = new TaskbarReplacementSession(dummyWindow, 0, autoHideEnabled: false))
        {
            check(restartSession.StopForRestart() is null, "StopForRestart on an idle session must succeed.");
            check(restartSession.StoppedForRestart, "StopForRestart must mark the session so Dispose skips native taskbar restore.");
        }
    }

    private static void CheckFlyoutState(Application application, Action<bool, string> check)
    {
        var anchor = new Button { Content = "Flyout anchor" };
        var host = new Window
        {
            Width = 300,
            Height = 180,
            Left = -20000,
            Top = -20000,
            ShowInTaskbar = false,
            ShowActivated = false,
            Content = anchor
        };
        var flyout = new DockFlyout();
        var secondFlyout = new DockFlyout();
        try
        {
            host.Show();
            PumpDispatcher(application.Dispatcher, 100);
            flyout.Show(anchor, new TextBlock { Text = "Calendar" }, animate: false, focusable: false);
            PumpDispatcher(application.Dispatcher, 100);
            check(flyout.IsOpen && DockFlyout.IsAnyOpen,
                "An open widget flyout must lock auto-hide.");

            flyout.Close();
            PumpDispatcher(application.Dispatcher, 100);
            check(!flyout.IsOpen && !DockFlyout.IsAnyOpen,
                "Closing the widget flyout must release the auto-hide lock.");

            flyout.Show(anchor, new TextBlock { Text = "Weather" }, animate: false, focusable: false);
            PumpDispatcher(application.Dispatcher, 100);
            secondFlyout.Show(anchor, new TextBlock { Text = "Tray" }, animate: false, focusable: false);
            PumpDispatcher(application.Dispatcher, 100);
            check(!flyout.IsOpen && secondFlyout.IsOpen && DockFlyout.IsAnyOpen,
                "Opening another widget flyout must close the old one without releasing the new lock.");
            DockFlyout.CloseAll();
            PumpDispatcher(application.Dispatcher, 100);
            check(!flyout.IsOpen && !secondFlyout.IsOpen && !DockFlyout.IsAnyOpen,
                "Closing all flyouts must release the auto-hide lock after switching widgets.");
        }
        finally
        {
            flyout.Close();
            secondFlyout.Close();
            host.Close();
        }
    }

    private static void CheckTrayTransientTooltips(Action<bool, string> check)
    {
        check(TrayIcons.IsTransientTooltip("Kaspersky Premium 21.27.6.532\r\nStarting...")
            && TrayIcons.IsTransientTooltip("Microsoft 365 and Office are installing in the background.")
            && TrayIcons.IsTransientTooltip("Uygulama başlatılıyor..."),
            "Transient startup/installer tray tooltips must fall back to live executable icons.");
        check(!TrayIcons.IsTransientTooltip("Everything") && !TrayIcons.IsTransientTooltip("NVIDIA Ayarları"),
            "Resting tray tooltips must keep their tray icon snapshots.");
    }

    private static void CheckSettingsSurviveReopen(Application application, Action<bool, string> check)
    {
        var path = Path.Combine(Path.GetTempPath(), $"opentaskbar-settings-reopen-{Guid.NewGuid():N}.json");
        try
        {
            var store = new DockSettingsStore(path, CultureInfo.GetCultureInfo("tr-TR"));
            var initial = new DockSettings(
                Language: AppLanguage.Turkish,
                Theme: AppTheme.Dark,
                AutoHide: AutoHideMode.Smart,
                StartPlacement: StartButtonPlacement.End,
                Size: DockSize.Compact,
                DockOpacity: 0.6,
                MenuOpacity: 0.6,
                WidgetOpacity: 0.6,
                SettingsOpacity: 0.6,
                LauncherHotkey: HotkeyChoice.CtrlSpace,
                DockHotkey: HotkeyChoice.WinAltD,
                Widgets: ["Clock", "Media", "SystemStats"],
                InstalledWidgets: ["Clock", "Media", "SystemStats"],
                ThemeApplicationIcons: false,
                ClickShowsWindowPicker: true);
            check(store.Save(initial) is null, "Initial custom dock settings must save cleanly.");

            var preferences = new ApplicationPreferences(store);
            var changeCount = 0;
            preferences.Changed += (_, _) => changeCount++;

            var sw1 = CreateOffscreenSettings(preferences);
            sw1.Show();
            PumpDispatcher(application.Dispatcher, 120);
            check(changeCount == 0, "Opening SettingsWindow must not mutate stored preferences.");

            sw1.Close();
            PumpUntil(application.Dispatcher, () => sw1.IsClosingOrClosed && !sw1.IsVisible, 1200);
            PumpDispatcher(application.Dispatcher, 220);

            var sw2 = CreateOffscreenSettings(preferences);
            try
            {
                sw2.Show();
                PumpDispatcher(application.Dispatcher, 120);
                if (FindFirstVisual<TabControl>(sw2) is { } tabs)
                {
                    for (var i = 0; i < tabs.Items.Count; i++)
                    {
                        tabs.SelectedIndex = i;
                        PumpDispatcher(application.Dispatcher, 30);
                    }
                }
            }
            finally
            {
                sw2.Close();
                PumpDispatcher(application.Dispatcher, 220);
            }

            var reloaded = store.Load();
            check(changeCount == 0,
                "Reopening SettingsWindow and browsing tabs after closing a prior instance must not fire spurious preference changes.");
            check(reloaded.Error is null, "Reloaded settings after SettingsWindow reopen must stay valid.");
            check(!reloaded.Settings.ThemeApplicationIcons,
                "Disabling themed application icons must survive settings reopen.");
            check(preferences.Current.ClickShowsWindowPicker && reloaded.Settings.ClickShowsWindowPicker,
                "Choosing a window on click must survive settings reopen.");
            check(!new DockSettings().ClickShowsWindowPicker,
                "A dock click must keep switching to the most recent window by default.");
            check(!MainWindow.ShouldShowWindowPicker(enabled: true, windowCount: 1)
                  && !MainWindow.ShouldShowWindowPicker(enabled: true, windowCount: 0),
                "An app with one window (or none) must activate or launch directly, never open the picker.");
            check(MainWindow.ShouldShowWindowPicker(enabled: true, windowCount: 2)
                  && !MainWindow.ShouldShowWindowPicker(enabled: false, windowCount: 3),
                "Several windows must open the picker only when the option is on.");
            check(preferences.Current.AutoHide == AutoHideMode.Smart && reloaded.Settings.AutoHide == AutoHideMode.Smart,
                "AutoHide preference must not reset when SettingsWindow is reopened.");
            check(preferences.Current.StartPlacement == StartButtonPlacement.End && reloaded.Settings.StartPlacement == StartButtonPlacement.End,
                "StartPlacement preference must not reset when SettingsWindow is reopened.");
            check(preferences.Current.LauncherHotkey == HotkeyChoice.CtrlSpace && reloaded.Settings.LauncherHotkey == HotkeyChoice.CtrlSpace,
                "LauncherHotkey preference must not reset when SettingsWindow is reopened.");
            check(preferences.Current.DockHotkey == HotkeyChoice.WinAltD && reloaded.Settings.DockHotkey == HotkeyChoice.WinAltD,
                "DockHotkey preference must not reset when SettingsWindow is reopened.");
            check(preferences.Current.Widgets?.SequenceEqual(["Clock", "Media", "SystemStats"]) == true
                && reloaded.Settings.Widgets?.SequenceEqual(["Clock", "Media", "SystemStats"]) == true,
                "Enabled widgets must not be wiped when SettingsWindow is reopened.");
            check(preferences.Current.InstalledWidgets?.SequenceEqual(["Clock", "Media", "SystemStats"]) == true
                && reloaded.Settings.InstalledWidgets?.SequenceEqual(["Clock", "Media", "SystemStats"]) == true,
                "Installed widgets must not be wiped when SettingsWindow is reopened.");
            check(Math.Abs(loadedOpacity(reloaded.Settings) - 0.6) < 0.001,
                "Per-surface opacity preferences must survive SettingsWindow reopen.");
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".tmp");
        }

        static double loadedOpacity(DockSettings s) => s.DockOpacity;

        static SettingsWindow CreateOffscreenSettings(ApplicationPreferences prefs) => new(prefs)
        {
            Left = -20000,
            Top = -20000,
            ShowActivated = false,
            ShowInTaskbar = false
        };
    }

    private static void CheckAutoHideLifecycleAndPointerSync(Application application, Action<bool, string> check)
    {
        var translate = new TranslateTransform();
        var surface = new Border { Width = 600, Height = 56, RenderTransform = translate };
        var host = new Window
        {
            Width = 600,
            Height = 76,
            Left = -20000,
            Top = -20000,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Content = surface
        };

        DockAutoHideController? controller = null;
        try
        {
            var hwnd = new WindowInteropHelper(host).EnsureHandle();
            host.Show();
            surface.Measure(new Size(600, 76));
            surface.Arrange(new Rect(0, 0, 600, 56));
            surface.UpdateLayout();

            var locked = true;
            var simulatedCursor = (X: 0, Y: 0);
            var waitBudgetMs = (int)DockMetrics.AutoHideDelayMs + 800;
            controller = new DockAutoHideController(host, hwnd, translate, surface, () => locked)
            {
                AnimationsEnabled = false,
                Edge = DockEdge.Bottom,
                DockBoundsProvider = () => (-10000, -10000, -9400, -9944),
                CursorPositionProvider = () => simulatedCursor
            };

            controller.SetMode(AutoHideMode.Always);
            PumpDispatcher(application.Dispatcher, (int)DockMetrics.AutoHideDelayMs + 150);
            check(!controller.IsHidden, "Interaction-locked dock must stay visible in auto-hide mode.");

            locked = false;
            controller.EvaluateState();
            PumpUntil(application.Dispatcher, () => controller.IsHidden, waitBudgetMs);
            check(controller.IsHidden && translate.Y >= DockMetrics.HideMinimumOffset,
                "Unlocked dock in Always auto-hide mode must slide off-screen after delay.");

            controller.OnMouseEnterEdge();
            check(!controller.IsHidden && Math.Abs(translate.Y) < 0.001,
                "Hovering the screen trigger edge must reveal the hidden dock.");

            controller.OnMouseLeaveEdge();
            PumpUntil(application.Dispatcher, () => controller.IsHidden, waitBudgetMs);
            check(controller.IsHidden, "Leaving the trigger edge must hide the dock again.");

            simulatedCursor = (-9700, -9970);
            controller.OnMouseEnterDock();
            check(!controller.IsHidden && Math.Abs(translate.Y) < 0.001,
                "Hovering the dock must reveal it.");
            controller.EvaluateState();
            check(controller.IsMouseOverDock && !controller.IsHidden,
                "EvaluateState must keep IsMouseOverDock while cursor stays inside dock bounds.");

            // Simulate missed MouseLeave once cursor moves outside (-10000, -10000, -9400, -9944).
            simulatedCursor = (0, 0);
            controller.EvaluateState();
            check(!controller.IsMouseOverDock,
                "EvaluateState must clear stuck IsMouseOverDock when cursor is outside dock bounds.");
            PumpUntil(application.Dispatcher, () => controller.IsHidden, waitBudgetMs);
            check(controller.IsHidden,
                "Dock must hide once stuck IsMouseOverDock is cleared by pointer sync.");

            // Long hover beyond the old 4-tick limit (simulate hovering for 1.3s, then moving away without MouseLeave)
            simulatedCursor = (-9700, -9970);
            controller.OnMouseEnterDock();
            check(!controller.IsHidden, "Dock must reveal on mouse enter.");
            PumpDispatcher(application.Dispatcher, 1100);
            check(!controller.IsHidden && controller.IsMouseOverDock, "Dock must stay revealed during sustained hover.");
            // Cursor now moves away, but simulate missed MouseLeave (do NOT call controller.EvaluateState or OnMouseLeave)
            simulatedCursor = (0, 0);
            // The continuous guard timer must automatically detect cursor departure and hide the dock
            PumpUntil(application.Dispatcher, () => controller.IsHidden, waitBudgetMs);
            check(controller.IsHidden, "Continuous guard timer must hide the dock after a long hover even if MouseLeave was dropped.");

            controller.SetMode(AutoHideMode.Smart);
            // Keep the same-process rule independent of whatever real window happens to be foreground on the CI box.
            controller.ForegroundProvider = () => hwnd;
            controller.CheckForegroundWindow(hwnd);
            // A hide tick queued by the previous phase can still run after the mode switch; let it settle.
            PumpUntil(application.Dispatcher, () => !controller.IsHidden, waitBudgetMs);
            check(!controller.HasOverlap && !controller.IsHidden,
                "Smart mode must ignore same-process foreground windows and stay visible without foreign overlap.");

            // Idle guard ticks must not repeatedly scan background windows. Window list changes mark the
            // overlap state dirty; the guard timer coalesces that work on its next tick.
            var bgQueries = 0;
            controller.BackgroundWindowsProvider = () => { bgQueries++; return []; };
            controller.CheckForegroundWindow(hwnd);
            bgQueries = 0;
            PumpDispatcher(application.Dispatcher, 650);
            check(bgQueries == 0,
                $"Idle guard ticks must not repeatedly invoke BackgroundWindowsProvider before the fallback scan (got {bgQueries}).");
            controller.InvalidateWindowOverlap();
            controller.CheckForegroundWindow(hwnd);
            PumpUntil(application.Dispatcher, () => bgQueries > 0, 1500);
            check(bgQueries == 1,
                $"InvalidateWindowOverlap must trigger one coalesced BackgroundWindowsProvider scan (got {bgQueries}).");
            controller.BackgroundWindowsProvider = null;

            controller.SetMode(AutoHideMode.Disabled);
            check(!controller.IsHidden && Math.Abs(translate.Y) < 0.001,
                "Disabling auto-hide must keep dock revealed at zero offset.");
        }
        finally
        {
            controller?.Dispose();
            host.Close();
        }
    }

    private static void CheckClockWidgetFlyoutAndAutoHide(Application application, Action<bool, string> check)
    {
        var translate = new TranslateTransform();
        var clock = new ClockWidget();
        var surface = new Border
        {
            Width = 600,
            Height = 56,
            Child = clock,
            RenderTransform = translate
        };
        var host = new Window
        {
            Width = 600,
            Height = 100,
            Content = surface,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None
        };

        DockAutoHideController? controller = null;
        try
        {
            var hwnd = new WindowInteropHelper(host).EnsureHandle();
            host.Show();
            surface.Measure(new Size(600, 76));
            surface.Arrange(new Rect(0, 0, 600, 56));
            surface.UpdateLayout();
            clock.Activate();

            var simulatedCursor = (X: -9700, Y: -9970);
            var waitBudgetMs = (int)DockMetrics.AutoHideDelayMs + 800;
            controller = new DockAutoHideController(host, hwnd, translate, surface, () => DockFlyout.IsAnyOpen)
            {
                AnimationsEnabled = false,
                Edge = DockEdge.Bottom,
                DockBoundsProvider = () => (-10000, -10000, -9400, -9944),
                CursorPositionProvider = () => simulatedCursor
            };
            controller.SetMode(AutoHideMode.Always);

            var clockBtn = (Button)clock.FindName("ClockButton");
            check(!DockFlyout.IsAnyOpen, "DockFlyout must start closed.");

            // 1. Click ClockButton to open calendar
            clockBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpDispatcher(application.Dispatcher, 100);
            check(DockFlyout.IsAnyOpen, "ClockButton click must open the calendar flyout.");

            // Cursor moves away: flyout remains open and keeps dock visible until user dismisses or toggles
            simulatedCursor = (0, 0);
            controller.EvaluateState();
            PumpDispatcher(application.Dispatcher, 200);
            check(DockFlyout.IsAnyOpen, "Open flyout must remain open while viewing even if cursor leaves.");
            check(!controller.IsHidden, "Dock must stay visible while a widget flyout is actively open.");

            // Toggling ClockButton closed closes the flyout and lets the dock auto-hide
            clockBtn.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent });
            clockBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpDispatcher(application.Dispatcher, 100);
            check(!DockFlyout.IsAnyOpen, "Toggling ClockButton with mouse down must close the flyout.");
            PumpUntil(application.Dispatcher, () => controller.IsHidden, waitBudgetMs);
            check(controller.IsHidden, "Dock must auto-hide after open flyout is closed.");

            // 2. Reveal dock, open flyout again, then close via DockFlyout.CloseAll (simulating outside click/dismiss)
            simulatedCursor = (-9700, -9970);
            controller.OnMouseEnterDock();
            check(!controller.IsHidden, "Hovering dock must reveal it.");
            PumpDispatcher(application.Dispatcher, 400);
            clockBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpDispatcher(application.Dispatcher, 100);
            check(DockFlyout.IsAnyOpen, "Clicking clock button must reopen calendar flyout.");

            // Clicking outside (CloseAll) must dismiss flyout and allow auto-hide
            DockFlyout.CloseAll();
            PumpDispatcher(application.Dispatcher, 400);
            check(!DockFlyout.IsAnyOpen, "Clicking outside must close the flyout.");

            // 3. Test toggle directly via Click while open
            clockBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpDispatcher(application.Dispatcher, 100);
            check(DockFlyout.IsAnyOpen, "Clicking clock button must reopen calendar flyout for toggle test.");
            clockBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpDispatcher(application.Dispatcher, 100);
            check(!DockFlyout.IsAnyOpen, "Clicking clock button again directly must toggle calendar flyout closed.");

            simulatedCursor = (0, 0);
            controller.EvaluateState();
            PumpUntil(application.Dispatcher, () => controller.IsHidden, waitBudgetMs);
            check(controller.IsHidden, "Dock must auto-hide after flyout was dismissed.");
        }
        finally
        {
            DockFlyout.CloseAll();
            controller?.Dispose();
            clock.Deactivate();
            host.Close();
        }
    }

    private static void PumpDispatcher(Dispatcher dispatcher, int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(milliseconds), DispatcherPriority.Background,
            (_, _) => frame.Continue = false, dispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    private static void PumpUntil(Dispatcher dispatcher, Func<bool> condition, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition() && Environment.TickCount64 < deadline)
        {
            PumpDispatcher(dispatcher, 35);
        }
    }

    private static void CheckLegacySettingsMigration(Action<bool, string> check)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"ot-legacy-migrate-{Guid.NewGuid():N}.json");
        try
        {
            // Realistic v0.2.x settings file with retired StatsMeters flags (CpuTemperature, Battery),
            // retired widgets (Memory, Search, Desktops, Tray, AiUsage), legacy SurfaceOpacity and DockBottomGap.
            File.WriteAllText(tempPath, """
                {
                  "Position": "Left",
                  "Theme": "Dark",
                  "SurfaceOpacity": 0.82,
                  "DockBottomGap": 10,
                  "DockScale": 1.1,
                  "Widgets": ["Clock", "Memory", "Search", "Desktops", "Tray", "AiUsage"],
                  "InstalledWidgets": ["Clock", "Memory", "Search", "Desktops", "Tray", "AiUsage"],
                  "AiUsageProviders": "Claude, Codex",
                  "StatsMeters": "Cpu, Memory, Battery, CpuTemperature, Gpu, GpuTemperature",
                  "ShowVirtualDesktops": true,
                  "ShowTray": true
                }
                """);

            var store = new DockSettingsStore(tempPath);
            var (migrated, error) = store.Load();
            check(error is null, $"Loading a v0.2.x settings file must migrate cleanly without error (got '{error}').");
            check(!migrated.MirrorMainDock && migrated.RevealDockOnWinKey && migrated.ClockDateLayout == ClockDateLayout.Beside,
                "Older settings must default MirrorMainDock=false, RevealDockOnWinKey=true, and ClockDateLayout=Beside.");
            check(store.Save(migrated with
                {
                    MirrorMainDock = true,
                    RevealDockOnWinKey = false,
                    ClockDateLayout = ClockDateLayout.Below
                }) is null,
                "Saving MirrorMainDock, RevealDockOnWinKey, and ClockDateLayout must succeed.");
            var reloaded = store.Load().Settings;
            check(reloaded.MirrorMainDock && !reloaded.RevealDockOnWinKey && reloaded.ClockDateLayout == ClockDateLayout.Below,
                "MirrorMainDock, RevealDockOnWinKey, and ClockDateLayout must survive settings reload.");
            check(migrated.Position == DockPosition.Left && migrated.Theme == AppTheme.Dark && Math.Abs(migrated.DockScale - 1.1) < 0.001,
                "Migration must preserve user position, theme, and dock scale.");
            check(migrated.StatsMeters == (SystemMeters.Cpu | SystemMeters.Memory | SystemMeters.Gpu | SystemMeters.GpuTemperature),
                "Migration must strip retired CpuTemperature and Battery flags while preserving active meters.");
            check(Math.Abs(migrated.DockOpacity - 0.82) < 0.001 && Math.Abs(migrated.WidgetOpacity - 0.82) < 0.001,
                "Migration must carry legacy SurfaceOpacity into per-surface opacities.");
            check(migrated.DockBottomGapPixels == 10,
                "Migration must carry legacy DockBottomGap into DockBottomGapPixels.");
            check(migrated.ShowSearchButton && migrated.ShowTaskViewButton && migrated.ShowTrayButton,
                "Migration must carry retired Search/Desktops/Tray widget states into shell button flags.");
            check(migrated.Widgets is not null && migrated.Widgets.Contains("Clock") && migrated.Widgets.Contains("AiUsageClaude") && migrated.Widgets.Contains("AiUsageCodex"),
                "Migration must split legacy AiUsage into enabled provider widgets.");

            // Numeric legacy StatsMeters bitmask (63 = all v0.2.x flags including CpuTemperature=8 and Battery=4)
            File.WriteAllText(tempPath, """{"StatsMeters": 63, "Position": "Right"}""");
            var (numericMigrated, numericError) = store.Load();
            check(numericError is null && numericMigrated.Position == DockPosition.Right
                && numericMigrated.StatsMeters == SystemMeters.All,
                "Numeric v0.2.x StatsMeters bitmasks must be masked to valid flags without resetting settings.");
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private static void CheckGapUnitsWeatherAndThemeEditor(Application application, Action<bool, string> check)
    {
        var path = Path.Combine(Path.GetTempPath(), $"opentaskbar-pr-c-{Guid.NewGuid():N}.json");
        var themesRoot = Path.Combine(Path.GetTempPath(), $"opentaskbar-pr-c-themes-{Guid.NewGuid():N}");
        var previousThemesRoot = ThemePacks.Root;
        try
        {
            // Bugs #3, #4, #6: Gap slider > 100px must not corrupt gap percent, external reload must refresh gap controls,
            // and non-Turkish weather city must not be overwritten with Turkey.
            var store = new DockSettingsStore(path, CultureInfo.GetCultureInfo("en-US"));
            var initial = new DockSettings(
                Language: AppLanguage.English,
                Theme: AppTheme.Dark,
                DockTopGapUnit: DockGapUnit.Pixels,
                DockTopGapPixels: 150,
                DockTopGapPercent: 25,
                DockBottomGapUnit: DockGapUnit.Pixels,
                DockBottomGapPixels: 165,
                DockBottomGapPercent: 35,
                DockHeightMode: DockHeightMode.Pixels,
                DockHeightPixels: 80,
                DockHeightPercent: 6,
                ShowWeather: true,
                WeatherCity: "Tokyo",
                Widgets: ["Clock", "Weather"],
                InstalledWidgets: ["Clock", "Weather"]);
            check(store.Save(initial) is null, "Initial settings with >100px gaps and non-Turkish city must save.");

            var preferences = new ApplicationPreferences(store);
            var sw = new SettingsWindow(preferences)
            {
                Left = -20000,
                Top = -20000,
                ShowActivated = false,
                ShowInTaskbar = false
            };
            try
            {
                sw.Show();
                PumpDispatcher(application.Dispatcher, 180);

                var countryBox = (ComboBox)sw.FindName("WeatherCountryBox");
                var cityCombo = (ComboBox)sw.FindName("WeatherCityCombo");
                var cityBox = (TextBox)sw.FindName("WeatherCityBox");
                check(countryBox.SelectedItem is null,
                    $"Non-Turkish custom city 'Tokyo' must not select Turkey (got '{countryBox.SelectedItem}').");
                check(cityCombo.Text == "Tokyo" && cityBox.Text == "Tokyo" && preferences.Current.WeatherCity == "Tokyo",
                    $"Non-Turkish custom city 'Tokyo' must be preserved in weather controls (got combo='{cityCombo.Text}', box='{cityBox.Text}').");
                check(countryBox is OpenTaskbar.Views.SearchableComboBox && cityCombo is OpenTaskbar.Views.SearchableComboBox,
                    "Weather country and city pickers must use the custom searchable dropdown with a dedicated glyph column.");

                // Bug #3: Trigger a preference edit while gap sliders are at 150px / 165px (>100); save must succeed and preserve percents.
                var animationsToggle = (CheckBox)sw.FindName("AnimationsToggle");
                animationsToggle.IsChecked = false;
                PumpDispatcher(application.Dispatcher, 60);
                check(!preferences.Current.AnimationsEnabled,
                    "Saving an unrelated setting when pixel gaps exceed 100px must succeed.");
                check(preferences.Current.DockTopGapPixels == 150 && preferences.Current.DockTopGapPercent == 25
                    && preferences.Current.DockBottomGapPixels == 165 && preferences.Current.DockBottomGapPercent == 35,
                    "Reading gap sliders in Pixels mode (>100px) must preserve saved DockTopGapPercent/DockBottomGapPercent.");

                // Switch gap units to Percent: sliders must switch to saved percent values (25% / 35%) and keep pixel values (150px / 165px).
                var topUnitBox = (ComboBox)sw.FindName("DockTopGapUnitBox");
                var bottomUnitBox = (ComboBox)sw.FindName("DockBottomGapUnitBox");
                var topSlider = (Slider)sw.FindName("DockTopGapSlider");
                var bottomSlider = (Slider)sw.FindName("DockBottomGapSlider");
                topUnitBox.SelectedItem = topUnitBox.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == nameof(DockGapUnit.Percent));
                bottomUnitBox.SelectedItem = bottomUnitBox.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == nameof(DockGapUnit.Percent));
                PumpDispatcher(application.Dispatcher, 60);
                check((int)Math.Round(topSlider.Value) == 25 && (int)Math.Round(bottomSlider.Value) == 35,
                    $"Switching gap unit to Percent must load saved percent values (got top={topSlider.Value}, bottom={bottomSlider.Value}).");
                check(preferences.Current.DockTopGapUnit == DockGapUnit.Percent
                    && preferences.Current.DockTopGapPixels == 150 && preferences.Current.DockTopGapPercent == 25
                    && preferences.Current.DockBottomGapUnit == DockGapUnit.Percent
                    && preferences.Current.DockBottomGapPixels == 165 && preferences.Current.DockBottomGapPercent == 35,
                    "Switching gap unit to Percent must save valid settings and preserve saved pixel values.");

                // The new height setting loads its saved values, switches units without losing the other one,
                // and hides the value row in Fill mode.
                var heightModeBox = (ComboBox)sw.FindName("DockHeightModeBox");
                var heightSlider = (Slider)sw.FindName("DockHeightSlider");
                var heightValueRow = (Border)sw.FindName("DockHeightValueRow");
                check((string)((ComboBoxItem)heightModeBox.SelectedItem!).Tag == nameof(DockHeightMode.Pixels)
                    && (int)Math.Round(heightSlider.Value) == 80 && heightValueRow.Visibility == Visibility.Visible,
                    "Saved Pixels height mode must load its slider value and show the value row.");
                heightModeBox.SelectedItem = heightModeBox.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == nameof(DockHeightMode.Percent));
                PumpDispatcher(application.Dispatcher, 60);
                check((int)Math.Round(heightSlider.Value) == 6 && preferences.Current.DockHeightMode == DockHeightMode.Percent
                    && preferences.Current.DockHeightPixels == 80,
                    "Switching height mode to Percent must load the saved percent value and preserve pixels.");
                heightModeBox.SelectedItem = heightModeBox.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == nameof(DockHeightMode.Fill));
                PumpDispatcher(application.Dispatcher, 60);
                check(heightValueRow.Visibility == Visibility.Collapsed && preferences.Current.DockHeightMode == DockHeightMode.Fill,
                    "Fill height mode must hide the value slider and persist the mode.");

                // Bug #6: External preference update must refresh gap controls via LoadAllSelections -> LoadDockGapControls.
                var externalUpdate = preferences.Current with
                {
                    DockTopGapUnit = DockGapUnit.Pixels,
                    DockTopGapPixels = 88,
                    DockTopGapPercent = 12,
                    DockBottomGapUnit = DockGapUnit.Percent,
                    DockBottomGapPixels = 110,
                    DockBottomGapPercent = 44,
                    WeatherCity = "Ankara"
                };
                check(preferences.Save(externalUpdate) is null, "External gap and weather update must save.");
                PumpDispatcher(application.Dispatcher, 180);
                check((int)Math.Round(topSlider.Value) == 88 && (int)Math.Round(bottomSlider.Value) == 44,
                    $"LoadAllSelections must refresh gap sliders on external update (got top={topSlider.Value}, bottom={bottomSlider.Value}).");
                check(countryBox.SelectedItem as string == "Turkey" && cityCombo.Text == "Ankara" && cityBox.Text == "Ankara",
                    "Turkish city 'Ankara' must match Turkey and preserve Ankara in weather controls.");
            }
            finally
            {
                sw.Close();
                PumpDispatcher(application.Dispatcher, 220);
            }

            // Bug #13: ThemePropertyEditor must read pack tokens from _widgetPack before RadiusManager.Derived overrides
            // and match pack IDs case-insensitively in SaveTheme().
            ThemePacks.Root = themesRoot;
            var packDir = Path.Combine(themesRoot, "My-Custom-Pack");
            var widgetDir = Path.Combine(packDir, ThemePack.WidgetFolder);
            Directory.CreateDirectory(widgetDir);
            File.WriteAllText(Path.Combine(packDir, ThemePack.ManifestFileName), """{"Name": "My Custom Pack", "Author": "Tester"}""");
            File.WriteAllText(Path.Combine(widgetDir, WidgetPack.ManifestFileName), """
                {
                  "name": "My Custom Pack Widgets",
                  "dark": {
                    "DockRadius": "14,14,14,14"
                  }
                }
                """);

            RadiusManager.Initialize();
            RadiusManager.Apply(32);
            var editor = new ThemePropertyEditor();
            editor.LoadTheme("MY-CUSTOM-PACK", AppTheme.Dark);
            var resolvedRadius = editor.ResolveTokenValue("DockRadius");
            check(resolvedRadius is CornerRadius { TopLeft: 14, TopRight: 14, BottomRight: 14, BottomLeft: 14 },
                $"ThemePropertyEditor must read DockRadius=14 from _widgetPack instead of RadiusManager.Derived=32 (got '{resolvedRadius}').");

            editor.UpdateToken("DockRadius", JsonValue.Create("16,16,16,16"), immediate: true);
            var savedPackJson = JsonNode.Parse(File.ReadAllText(Path.Combine(widgetDir, WidgetPack.ManifestFileName)))?.AsObject();
            check(savedPackJson?["dark"]?["DockRadius"]?.GetValue<string>() == "16,16,16,16",
                "ThemePropertyEditor.SaveTheme must match theme ID case-insensitively and persist updated token to widgets/pack.json.");
        }
        finally
        {
            RadiusManager.Apply(20);
            ThemePacks.Select(null);
            ThemePacks.Root = previousThemesRoot;
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(themesRoot)) Directory.Delete(themesRoot, recursive: true);
        }
    }

    private static void CheckSettingsWindowCornerAndBorder(Application application, Action<bool, string> check)
    {
        check(SettingsBackdrop.NativeCornerRadius == 8, "Settings window native corner radius must match Windows 11's 8 DIP.");
        check(SettingsBackdrop.DwmwaBorderColor == 34 && SettingsBackdrop.DwmwaColorNone == unchecked((int)0xFFFFFFFE),
            "SettingsBackdrop must suppress the native DWM outer border with DWMWA_BORDER_COLOR = DWMWA_COLOR_NONE.");

        var path = Path.Combine(Path.GetTempPath(), $"opentaskbar-corners-{Guid.NewGuid():N}.json");
        try
        {
            var store = new DockSettingsStore(path, CultureInfo.GetCultureInfo("en-US"));
            var initial = new DockSettings(
                Theme: AppTheme.Dark,
                AnimationsEnabled: false,
                SettingsBackground: SurfaceBackground.Glass,
                SettingsOpacity: 0.92,
                Widgets: ["Clock"],
                InstalledWidgets: ["Clock"]);
            check(store.Save(initial) is null, "Initial settings for corner check must save.");

            var preferences = new ApplicationPreferences(store);
            ThemeManager.Apply(preferences.Current.Theme, preferences.Current);
            var sw = new SettingsWindow(preferences)
            {
                Left = -20000,
                Top = -20000,
                ShowActivated = false,
                ShowInTaskbar = false
            };
            try
            {
                sw.Show();
                PumpDispatcher(application.Dispatcher, 120);

                check(sw.SettingsFrame.CornerRadius == new CornerRadius(0)
                    && sw.SettingsFrame.BorderThickness == new Thickness(0),
                    $"SettingsFrame must stay square with no border under the DWM clip (got radius={sw.SettingsFrame.CornerRadius}, thickness={sw.SettingsFrame.BorderThickness}).");

                var expectedRadius = SettingsBackdrop.SupportsNativeRoundedCorners ? new CornerRadius(8) : new CornerRadius(0);
                check(!sw.SettingsBorderOverlay.IsHitTestVisible
                    && sw.SettingsBorderOverlay.CornerRadius == expectedRadius,
                    $"SettingsBorderOverlay must be hit-test invisible and match corner radius {expectedRadius} (got {sw.SettingsBorderOverlay.CornerRadius}).");

                var maximizedTrigger = sw.SettingsBorderOverlay.Style?.Triggers
                    .OfType<DataTrigger>()
                    .FirstOrDefault(t => Equals(t.Value, WindowState.Maximized)
                        || string.Equals(t.Value?.ToString(), nameof(WindowState.Maximized), StringComparison.Ordinal));
                var hasZeroCornerSetter = maximizedTrigger?.Setters
                    .OfType<Setter>()
                    .Any(s => ReferenceEquals(s.Property, Border.CornerRadiusProperty)
                        && ((s.Value is CornerRadius cr && cr == new CornerRadius(0)) || s.Value?.ToString() == "0")) == true;
                check(hasZeroCornerSetter, "SettingsBorderOverlay must switch to CornerRadius(0) when maximized.");
            }
            finally
            {
                sw.Close();
                PumpDispatcher(application.Dispatcher, 120);
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void CheckSettingsWindowPreviewDensityAndAntigravityGroups(Application application, Action<bool, string> check)
    {
        var path = Path.Combine(Path.GetTempPath(), $"opentaskbar-ai-settings-{Guid.NewGuid():N}.json");
        try
        {
            var store = new DockSettingsStore(path, CultureInfo.GetCultureInfo("en-US"));
            var initial = new DockSettings(
                Language: AppLanguage.English,
                Theme: AppTheme.Dark,
                AnimationsEnabled: false,
                Widgets: ["Clock", "Weather", "AiUsageClaude", "AiUsageAntigravity"],
                InstalledWidgets: ["Clock", "Weather", "AiUsageClaude", "AiUsageAntigravity"]);
            check(store.Save(initial) is null, "Initial settings for preview density check must save.");

            var preferences = new ApplicationPreferences(store);
            var sw = new SettingsWindow(preferences)
            {
                Left = -20000,
                Top = -20000,
                ShowActivated = false,
                ShowInTaskbar = false
            };
            try
            {
                sw.Show();
                PumpDispatcher(application.Dispatcher, 120);

                // Select WidgetsTab so live previews attach
                var tabs = (TabControl)sw.FindName("SettingsTabs");
                var widgetsTab = (TabItem)sw.FindName("WidgetsTab");
                tabs.SelectedItem = widgetsTab;
                PumpDispatcher(application.Dispatcher, 120);

                var densityBox = sw.FindName("DensityBox") as ComboBox
                    ?? throw new InvalidOperationException("Widgets tab must expose the global widget density dropdown.");
                check(typeof(SettingsWindow).GetField("_widgetDensityBoxes",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance) is null,
                    "Per-widget density dropdowns must be removed in favour of the global density setting.");

                var optionsField = typeof(SettingsWindow).GetField("_widgetOptions",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                var options = (System.Collections.IEnumerable)optionsField.GetValue(sw)!;
                DockWidgetOption? claudeOption = null;
                DockWidgetOption? agyOption = null;
                foreach (DockWidgetOption opt in options)
                {
                    if (opt.Id == "AiUsageClaude") claudeOption = opt;
                    else if (opt.Id == "AiUsageAntigravity") agyOption = opt;
                }
                check(claudeOption?.LivePreview is OpenTaskbar.Widgets.DockWidget && agyOption?.LivePreview is OpenTaskbar.Widgets.DockWidget,
                    "Live preview widgets must be attached for Claude and Antigravity.");

                var claudePreview = (OpenTaskbar.Widgets.DockWidget)claudeOption!.LivePreview!;
                check(!claudePreview.IsCompact, "Claude live preview must start in Full density when global density is Auto.");

                // Global density must be the only density control: Compact compacts every live preview.
                densityBox.SelectedItem = densityBox.Items.OfType<ComboBoxItem>()
                    .First(i => (string)i.Tag == nameof(WidgetDensityMode.Compact));
                PumpDispatcher(application.Dispatcher, 60);
                check(claudePreview.IsCompact && preferences.Current.GlobalWidgetDensity == WidgetDensityMode.Compact
                        && preferences.Current.WidgetDensityOverrides is null,
                    "Changing global density to Compact must compact live previews and clear legacy overrides.");

                // Sync _aiRingBoxes across all AI cards when one ring box changes
                var ringBoxesField = typeof(SettingsWindow).GetField("_aiRingBoxes",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                var ringBoxes = (List<ComboBox>)ringBoxesField.GetValue(sw)!;
                check(ringBoxes.Count >= 2, "Every AI usage card must have an AiUsageRing ComboBox.");
                ringBoxes[0].SelectedItem = ringBoxes[0].Items.OfType<ComboBoxItem>()
                    .First(i => (string)i.Tag == nameof(AiUsageRing.Session));
                PumpDispatcher(application.Dispatcher, 60);
                check(preferences.Current.AiUsageRing == AiUsageRing.Session
                    && ringBoxes.All(cb => (cb.SelectedItem as ComboBoxItem)?.Tag as string == nameof(AiUsageRing.Session)),
                    "Changing AiUsageRing on one AI card must sync all sibling AI ring ComboBoxes.");

                // Antigravity group checkboxes: uncheck Argon and Claude, then verify last checked box (Gemini) cannot be unchecked
                var agyBoxesField = typeof(SettingsWindow).GetField("_antigravityGroupBoxes",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                var agyBoxes = (Dictionary<AntigravityGroups, CheckBox>)agyBoxesField.GetValue(sw)!;
                check(agyBoxes.Count == 3 && agyBoxes.Values.All(b => b.IsChecked == true),
                    "AiUsageAntigravity settings must attach 3 checked group checkboxes (Gemini, Claude, Argon).");

                agyBoxes[AntigravityGroups.Argon].IsChecked = false;
                PumpDispatcher(application.Dispatcher, 40);
                check(preferences.Current.AntigravityGroups == (AntigravityGroups.Gemini | AntigravityGroups.Claude),
                    "Unchecking Argon must persist Gemini | Claude.");

                agyBoxes[AntigravityGroups.Claude].IsChecked = false;
                PumpDispatcher(application.Dispatcher, 40);
                check(preferences.Current.AntigravityGroups == AntigravityGroups.Gemini,
                    "Unchecking Claude must persist Gemini.");

                // Attempt to uncheck the last active group (Gemini) -> must remain checked
                agyBoxes[AntigravityGroups.Gemini].IsChecked = false;
                PumpDispatcher(application.Dispatcher, 40);
                check(agyBoxes[AntigravityGroups.Gemini].IsChecked == true
                    && preferences.Current.AntigravityGroups == AntigravityGroups.Gemini,
                    "Attempting to uncheck the last active Antigravity group checkbox must keep it checked.");
            }
            finally
            {
                sw.Close();
                PumpDispatcher(application.Dispatcher, 120);
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void CheckStartupRegistrationSyncAndParsing(Action<bool, string> check)
    {
        // 1. Run\OpenTaskbar command-line unquoting and argument splitting.
        var (emptyExe, emptyArgs) = StartupRegistration.ParseRunCommand(null);
        check(emptyExe is null && emptyArgs == string.Empty, "Null startup command must parse to null executable.");
        check(StartupRegistration.ParseRunCommand("   ").ExecutablePath is null
            && StartupRegistration.ParseRunCommand("\"\"").ExecutablePath is null
            && StartupRegistration.ParseRunCommand("\"   \"").ExecutablePath is null,
            "Whitespace and empty-quoted startup commands must parse to null executable.");

        var (quotedExe, quotedArgs) = StartupRegistration.ParseRunCommand("\"C:\\Program Files\\OpenTaskbar\\OpenTaskbar.exe\"");
        check(quotedExe == @"C:\Program Files\OpenTaskbar\OpenTaskbar.exe" && quotedArgs == string.Empty,
            $"Quoted path with spaces must unquote cleanly (got exe='{quotedExe}', args='{quotedArgs}').");

        var (unicodeExe, unicodeArgs) = StartupRegistration.ParseRunCommand(
            "  \"C:\\Users\\Ömer Şahin\\Masaüstü\\100% Yeni\\OpenTaskbar.exe\"  --settings --tag \"v0.3.3\"  ");
        check(unicodeExe == @"C:\Users\Ömer Şahin\Masaüstü\100% Yeni\OpenTaskbar.exe"
            && unicodeArgs == "--settings --tag \"v0.3.3\"",
            $"Quoted Unicode path with arguments must preserve path and args (got exe='{unicodeExe}', args='{unicodeArgs}').");

        var (unquotedExe, unquotedArgs) = StartupRegistration.ParseRunCommand(@"C:\Apps\OpenTaskbar\OpenTaskbar.exe --silent");
        check(unquotedExe == @"C:\Apps\OpenTaskbar\OpenTaskbar.exe" && unquotedArgs == "--silent",
            $"Unquoted command with args must separate executable and args (got exe='{unquotedExe}', args='{unquotedArgs}').");

        var (unquotedSpacedExe, unquotedSpacedArgs) = StartupRegistration.ParseRunCommand(
            @"C:\Program Files\OpenTaskbar\OpenTaskbar.exe --flag other.exe");
        check(unquotedSpacedExe == @"C:\Program Files\OpenTaskbar\OpenTaskbar.exe" && unquotedSpacedArgs == "--flag other.exe",
            $"Unquoted path with spaces must stop at the executable boundary (got exe='{unquotedSpacedExe}', args='{unquotedSpacedArgs}').");

        var (unclosedExe, unclosedArgs) = StartupRegistration.ParseRunCommand("\"C:\\Apps\\OpenTaskbar.exe");
        check(unclosedExe == @"C:\Apps\OpenTaskbar.exe" && unclosedArgs == string.Empty,
            $"Unclosed leading quote must degrade cleanly (got exe='{unclosedExe}', args='{unclosedArgs}').");

        var (nonExe, nonExeArgs) = StartupRegistration.ParseRunCommand(@"C:\Apps\launch.cmd /c run");
        check(nonExe == @"C:\Apps\launch.cmd" && nonExeArgs == "/c run",
            $"Non-.exe unquoted command must split on first whitespace (got exe='{nonExe}', args='{nonExeArgs}').");

        // 2. StartupApproved\Run REG_BINARY enabled/disabled detection (odd first byte = disabled by Task Manager).
        check(StartupRegistration.IsStartupApprovedEnabled(null), "Missing StartupApproved entry must be treated as enabled.");
        check(StartupRegistration.IsStartupApprovedEnabled([]), "Empty StartupApproved entry must be treated as enabled.");
        check(StartupRegistration.IsStartupApprovedEnabled([0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]),
            "0x02 StartupApproved entry must be treated as enabled.");
        check(StartupRegistration.IsStartupApprovedEnabled([0x00, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0])
            && StartupRegistration.IsStartupApprovedEnabled([0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]),
            "Even first bytes in StartupApproved must be treated as enabled.");
        check(!StartupRegistration.IsStartupApprovedEnabled([0x03, 0, 0, 0, 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80]),
            "0x03 StartupApproved entry (Task Manager disabled) must be treated as disabled.");
        check(!StartupRegistration.IsStartupApprovedEnabled([0x01, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0])
            && !StartupRegistration.IsStartupApprovedEnabled([0x07, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]),
            "Odd first bytes in StartupApproved must be treated as disabled.");

        // 3. Pure Read() and SyncOnStartup decision logic (no real HKCU mutation).
        var enabledBytes = new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        var disabledBytes = new byte[] { 0x03, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 };
        check(StartupRegistration.IsRegisteredAndActive("\"C:\\Apps\\OpenTaskbar.exe\"", null, _ => true),
            "Existing Run target with no StartupApproved override must read as enabled.");
        check(StartupRegistration.IsRegisteredAndActive("\"C:\\Apps\\OpenTaskbar.exe\"", enabledBytes, _ => true),
            "Existing Run target with enabled StartupApproved entry must read as enabled.");
        check(!StartupRegistration.IsRegisteredAndActive("\"C:\\Apps\\OpenTaskbar.exe\"", disabledBytes, _ => true),
            "Run target disabled in StartupApproved must read as disabled.");
        check(!StartupRegistration.IsRegisteredAndActive("\"C:\\Apps\\OpenTaskbar.exe\"", null, _ => false),
            "Run target whose executable no longer exists on disk must read as disabled.");
        check(!StartupRegistration.IsRegisteredAndActive("OpenTaskbar.exe", null, _ => true),
            "Relative Run target path must not read as enabled.");

        check(!StartupRegistration.ShouldUpdateRunRegistration(null, null, @"C:\Apps\v2\OpenTaskbar.exe"),
            "Sync must not create a Run entry when startup is disabled.");
        check(!StartupRegistration.ShouldUpdateRunRegistration("\"C:\\Apps\\v1\\OpenTaskbar.exe\"", disabledBytes, @"C:\Apps\v2\OpenTaskbar.exe"),
            "Sync must not overwrite a Run entry disabled in StartupApproved.");
        check(!StartupRegistration.ShouldUpdateRunRegistration("\"C:\\Apps\\v2\\OpenTaskbar.exe\"", enabledBytes, @"c:\apps\v2\opentaskbar.exe"),
            "Sync must skip rewriting Run when the normalized path already matches case-insensitively.");
        check(StartupRegistration.ShouldUpdateRunRegistration("\"C:\\Apps\\v1\\OpenTaskbar.exe\"", null, @"C:\Apps\v2\OpenTaskbar.exe"),
            "Sync must update an active Run entry that points to an older OpenTaskbar.exe location.");

        // 4. Executable path validation, long-path bound (>260 up to 1024), and directory boundary checks.
        check(!StartupRegistration.IsUnderDirectory(@"C:\Temp2\OpenTaskbar.exe", @"C:\Temp"),
            "IsUnderDirectory must not match sibling prefix directories like C:\\Temp2 against C:\\Temp.");
        check(StartupRegistration.IsUnderDirectory(@"C:\Temp\Sub\OpenTaskbar.exe", @"C:\Temp\"),
            "IsUnderDirectory must match nested paths under the target directory.");

        var longAllowedPath = @"C:\" + new string('a', 100) + @"\" + new string('b', 100) + @"\" + new string('c', 100) + @"\OpenTaskbar.exe";
        var tooLongPath = @"C:\" + new string('a', StartupRegistration.MaxCommandLength) + @"\OpenTaskbar.exe";
        check(longAllowedPath.Length > 260 && longAllowedPath.Length + 2 <= StartupRegistration.MaxCommandLength
            && StartupRegistration.IsValidStartupExecutable(longAllowedPath, @"C:\Temp", _ => true),
            "Startup executable validation must allow valid paths longer than 260 chars up to MaxCommandLength.");
        check(!StartupRegistration.IsValidStartupExecutable(tooLongPath, @"C:\Temp", _ => true),
            "Startup executable validation must reject paths exceeding MaxCommandLength.");
        check(!StartupRegistration.IsValidStartupExecutable(@"C:\Program Files\dotnet\dotnet.exe", @"C:\Temp", _ => true),
            "Startup executable validation must reject dotnet.exe.");

        // 5. Temp-folder executable checks and .lnk startup shortcut retargeting.
        var tempRoot = Path.Combine(Path.GetTempPath(), $"ot-startup-sync-{Guid.NewGuid():N}");
        try
        {
            var oldDir = Path.Combine(tempRoot, "v0.3.2");
            var newDir = Path.Combine(tempRoot, "v0.3.3 Masaüstü");
            var otherDir = Path.Combine(tempRoot, "Other");
            var startupDir = Path.Combine(tempRoot, "Startup");
            Directory.CreateDirectory(oldDir);
            Directory.CreateDirectory(newDir);
            Directory.CreateDirectory(otherDir);
            Directory.CreateDirectory(startupDir);

            var oldExe = Path.Combine(oldDir, "OpenTaskbar.exe");
            var deletedDir = Path.Combine(tempRoot, "v0.3.1-deleted");
            Directory.CreateDirectory(deletedDir);
            var deletedExe = Path.Combine(deletedDir, "OpenTaskbar.exe");
            var newExe = Path.Combine(newDir, "OpenTaskbar.exe");
            var dotnetExe = Path.Combine(otherDir, "dotnet.exe");
            var companionExe = Path.Combine(otherDir, "Companion.exe");
            File.WriteAllText(oldExe, string.Empty);
            File.WriteAllText(deletedExe, string.Empty);
            File.WriteAllText(newExe, string.Empty);
            File.WriteAllText(dotnetExe, string.Empty);
            File.WriteAllText(companionExe, string.Empty);

            check(!StartupRegistration.IsValidStartupExecutable(newExe, Path.GetTempPath()),
                "Sync executable validation must reject OpenTaskbar.exe running under Path.GetTempPath().");
            check(StartupRegistration.IsValidStartupExecutable(newExe, disallowedRootDirectory: null),
                "Explicit SetEnabled validation must accept an existing OpenTaskbar.exe path.");
            check(!StartupRegistration.IsValidStartupExecutable(dotnetExe, disallowedRootDirectory: null),
                "Startup executable validation must reject an existing dotnet.exe file.");

            var staleLink = Path.Combine(startupDir, "OpenTaskbar.lnk");
            var deletedTargetLink = Path.Combine(startupDir, "OpenTaskbar Old.lnk");
            var currentLink = Path.Combine(startupDir, "OpenTaskbar - Current.lnk");
            var companionLink = Path.Combine(startupDir, "OpenTaskbar Companion.lnk");
            var unrelatedLink = Path.Combine(startupDir, "OtherApp.lnk");

            CreateTestShortcut(staleLink, oldExe, oldDir, "--settings");
            CreateTestShortcut(deletedTargetLink, deletedExe, deletedDir, string.Empty);
            Directory.Delete(deletedDir, recursive: true);
            CreateTestShortcut(currentLink, newExe, newDir, string.Empty);
            CreateTestShortcut(companionLink, companionExe, otherDir, string.Empty);
            CreateTestShortcut(unrelatedLink, oldExe, oldDir, string.Empty);

            var updated = StartupRegistration.SyncStartupFolderShortcuts(startupDir, newExe);
            check(updated == 2, $"Startup shortcut sync must retarget exactly 2 stale OpenTaskbar*.lnk shortcuts (got {updated}).");

            var (staleTarget, staleWorkDir, staleArgs) = ReadTestShortcut(staleLink);
            check(string.Equals(StartupRegistration.TryNormalizePath(staleTarget), StartupRegistration.TryNormalizePath(newExe), StringComparison.OrdinalIgnoreCase)
                && string.Equals(StartupRegistration.TryNormalizePath(staleWorkDir), StartupRegistration.TryNormalizePath(newDir), StringComparison.OrdinalIgnoreCase)
                && staleArgs == "--settings",
                $"Stale OpenTaskbar.lnk must update TargetPath and WorkingDirectory while keeping Arguments (got target='{staleTarget}', wd='{staleWorkDir}', args='{staleArgs}').");

            var (healedTarget, healedWorkDir, _) = ReadTestShortcut(deletedTargetLink);
            check(string.Equals(StartupRegistration.TryNormalizePath(healedTarget), StartupRegistration.TryNormalizePath(newExe), StringComparison.OrdinalIgnoreCase)
                && string.Equals(StartupRegistration.TryNormalizePath(healedWorkDir), StartupRegistration.TryNormalizePath(newDir), StringComparison.OrdinalIgnoreCase),
                $"Stale shortcut pointing to a deleted old OpenTaskbar.exe must also be retargeted (got target='{healedTarget}', wd='{healedWorkDir}').");

            var (companionTarget, _, _) = ReadTestShortcut(companionLink);
            check(string.Equals(StartupRegistration.TryNormalizePath(companionTarget), StartupRegistration.TryNormalizePath(companionExe), StringComparison.OrdinalIgnoreCase),
                $"OpenTaskbar Companion.lnk pointing to Companion.exe must remain untouched (got '{companionTarget}').");

            var (unrelatedTarget, _, _) = ReadTestShortcut(unrelatedLink);
            check(string.Equals(StartupRegistration.TryNormalizePath(unrelatedTarget), StartupRegistration.TryNormalizePath(oldExe), StringComparison.OrdinalIgnoreCase),
                $"Unrelated OtherApp.lnk must remain untouched (got '{unrelatedTarget}').");

            var secondPass = StartupRegistration.SyncStartupFolderShortcuts(startupDir, newExe);
            check(secondPass == 0, $"Second startup shortcut sync pass must be a no-op (got {secondPass}).");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }

        static void CreateTestShortcut(string shortcutPath, string targetPath, string workingDirectory, string arguments)
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell")
                ?? throw new InvalidOperationException("WScript.Shell ProgID unavailable.");
            object? shell = null;
            object? link = null;
            try
            {
                shell = Activator.CreateInstance(shellType)
                    ?? throw new InvalidOperationException("Failed to instantiate WScript.Shell.");
                link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [shortcutPath])
                    ?? throw new InvalidOperationException("Failed to create test shortcut.");
                var linkType = link.GetType();
                linkType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, [targetPath]);
                linkType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, [workingDirectory]);
                if (arguments.Length > 0)
                    linkType.InvokeMember("Arguments", BindingFlags.SetProperty, null, link, [arguments]);
                linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            }
            finally
            {
                if (link is not null && Marshal.IsComObject(link)) _ = Marshal.FinalReleaseComObject(link);
                if (shell is not null && Marshal.IsComObject(shell)) _ = Marshal.FinalReleaseComObject(shell);
            }
        }

        static (string TargetPath, string WorkingDirectory, string Arguments) ReadTestShortcut(string shortcutPath)
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell")
                ?? throw new InvalidOperationException("WScript.Shell ProgID unavailable.");
            object? shell = null;
            object? link = null;
            try
            {
                shell = Activator.CreateInstance(shellType)
                    ?? throw new InvalidOperationException("Failed to instantiate WScript.Shell.");
                link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [shortcutPath])
                    ?? throw new InvalidOperationException("Failed to open test shortcut.");
                var linkType = link.GetType();
                var target = linkType.InvokeMember("TargetPath", BindingFlags.GetProperty, null, link, null) as string ?? string.Empty;
                var workDir = linkType.InvokeMember("WorkingDirectory", BindingFlags.GetProperty, null, link, null) as string ?? string.Empty;
                var args = linkType.InvokeMember("Arguments", BindingFlags.GetProperty, null, link, null) as string ?? string.Empty;
                return (target, workDir, args);
            }
            finally
            {
                if (link is not null && Marshal.IsComObject(link)) _ = Marshal.FinalReleaseComObject(link);
                if (shell is not null && Marshal.IsComObject(shell)) _ = Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    private static T? FindFirstVisual<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            var nested = FindFirstVisual<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }
}
