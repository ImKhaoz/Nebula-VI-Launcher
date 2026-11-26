using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace XboxEmulatorUI;

public partial class MainWindow : Window
{
#if NEBULA_GTA_ONLY
    private static bool GtaOnlyEdition => true;
    private const string DataFolderName = "Nebula-GTAVI";
#else
    private static bool GtaOnlyEdition => false;
    private const string DataFolderName = "NexusLauncher";
#endif
    private readonly string _dataFile = Path.Combine(LauncherDataRoot, "library.json");
    private readonly string _settingsFile = Path.Combine(LauncherDataRoot, "settings.json");
    private static string LauncherDataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DataFolderName);
    private readonly List<GameEntry> _games = [];
    private string? _xvalkyriePath;
    private string _gtaRuntime = "victory";
    private bool _xclFrameDiagnostics;
    private bool _xclPostRunPrecompile = true;
    private bool _recentOnly;
    private bool _startupSound = true;
    private bool _showIntro = true;
    private bool _recordingDelay;
    private readonly DispatcherTimer _recordingDelayTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private long _recordingDelayStarted;
    private bool _backgroundMotion = true;
    private bool _videoPreview = true;
    private string _defaultLayout = "carousel";
    private System.Media.SoundPlayer? _introSound;
    private bool _introFinished;
    private GameEntry? _featured;
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    public MainWindow()
    {
        InitializeComponent();
        if (GtaOnlyEdition) Title = "Nebula — GTA VI";
        Closed += (_, _) => _victoryPrewarmCancellation?.Cancel();
        WindowAppearance.UseDarkFrame(this, borderless: true);
        var artwork = Path.Combine(AppContext.BaseDirectory, "Assets", "NexusBackdrop.png");
        if (File.Exists(artwork))
            Backdrop.Source = SplashBackdrop.Source = new BitmapImage(new Uri(artwork));
        SplashWordmark.Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "NebulaWordmark.png")));
        HeaderWordmark.Source = SplashWordmark.Source;
        var logo = Path.Combine(AppContext.BaseDirectory, "Assets", "NexusLogo.png");
        if (File.Exists(logo))
        {
            var mark = new BitmapImage(new Uri(logo));
            HeaderLogo.Source = mark;
            HeaderWordmark.Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "NebulaHeaderLettering.png")));
            SplashLogo.Source = mark;
            SplashSheenMask.ImageSource = mark;
            SplashFallback.Visibility = Visibility.Collapsed;
        }
        _toastTimer.Tick += (_, _) => { Toast.Visibility = Visibility.Collapsed; _toastTimer.Stop(); };
        LoadLibrary();
        if (GtaOnlyEdition)
        {
            var game = _games.FirstOrDefault(entry => entry.IsLeonida) ?? new GameEntry { Name = "Grand Theft Auto VI", LaunchKind = "Leonida" };
            _games.Clear();
            _games.Add(game);
        }
        LoadSettings();
        if (GtaOnlyEdition)
        {
            _games[0].ExecutablePath = GetLeonidaCandidate() ?? "";
            if (FindGtaRuntime(_gtaRuntime) is null)
                _gtaRuntime = VictoryBuilds.Keys.FirstOrDefault(id => FindVictoryRuntime(id) is not null) ?? "victory";
        }
        _leonidaAvailabilityTimer.Tick += (_, _) => RefreshLeonidaAvailability();
        _leonidaAvailabilityTimer.Start();
        RefreshLibrary();
        RefreshRuntimeStatus();
        Loaded += (_, _) =>
        {
            StartStartupIntro();
            _ = InitializeDashboardAsync();
        };
    }

    private void StartStartupIntro()
    {
        if (!_showIntro || !SystemParameters.ClientAreaAnimation)
        {
            DismissSplash();
            return;
        }
        if (!_recordingDelay)
        {
            PlaySplash();
            return;
        }
        RecordingSeconds.Text = "10";
        RecordingLeadIn.Visibility = Visibility.Visible;
        _recordingDelayStarted = Stopwatch.GetTimestamp();
        _recordingDelayTimer.Tick += UpdateRecordingDelay;
        _recordingDelayTimer.Start();
    }

    private void UpdateRecordingDelay(object? sender, EventArgs e)
    {
        var seconds = 10 - Stopwatch.GetElapsedTime(_recordingDelayStarted).TotalSeconds;
        if (seconds <= 0) EndRecordingDelay();
        else RecordingSeconds.Text = Math.Ceiling(seconds).ToString();
    }

    private void EndRecordingDelay()
    {
        _recordingDelayTimer.Stop();
        RecordingLeadIn.Visibility = Visibility.Collapsed;
        PlaySplash();
    }

    private void PlaySplash()
    {
        if (_startupSound)
        {
            var audio = Path.Combine(AppContext.BaseDirectory, "Assets", "NexusStartup.wav");
            if (File.Exists(audio))
            {
                try
                {
                    _introSound = new System.Media.SoundPlayer(audio);
                    _introSound.Play();
                }
                catch (Exception error) when (error is InvalidOperationException or IOException) { }
            }
        }
        SplashMark.BeginAnimation(OpacityProperty, Reveal(0, 1, 90, 640));
        SplashZoom.BeginAnimation(ScaleTransform.ScaleXProperty, Reveal(.88, 1, 90, 1150));
        SplashZoom.BeginAnimation(ScaleTransform.ScaleYProperty, Reveal(.88, 1, 90, 1150));
        SplashGlow.BeginAnimation(OpacityProperty, Reveal(0, .86, 0, 1050));
        SplashSweep.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(-410, 410, TimeSpan.FromMilliseconds(900)) { BeginTime = TimeSpan.FromMilliseconds(360) });
        SplashCaption.BeginAnimation(OpacityProperty, Reveal(0, 1, 500, 420));
        SplashLineScale.BeginAnimation(ScaleTransform.ScaleXProperty, Reveal(0, 1, 620, 650));
        _ = FinishStartupIntroAsync();
    }

    private async Task FinishStartupIntroAsync()
    {
        await Task.Delay(2200);
        DismissSplash();
    }

    private static DoubleAnimation Reveal(double from, double to, int delay, int duration)
        => new(from, to, TimeSpan.FromMilliseconds(duration))
        {
            BeginTime = TimeSpan.FromMilliseconds(delay),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

    private void DismissSplash()
    {
        if (_introFinished) return;
        _introFinished = true;
        _recordingDelayTimer.Stop();
        RecordingLeadIn.Visibility = Visibility.Collapsed;
        SplashMark.BeginAnimation(OpacityProperty, null);
        SplashMark.Opacity = 1;
        SplashZoom.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        SplashZoom.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        SplashZoom.ScaleX = SplashZoom.ScaleY = 1;
        SplashSweep.BeginAnimation(TranslateTransform.XProperty, null);
        SplashGlow.BeginAnimation(OpacityProperty, null);
        SplashGlow.Opacity = .86;
        SplashCaption.BeginAnimation(OpacityProperty, null);
        SplashCaption.Opacity = 1;
        SplashLineScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        SplashLineScale.ScaleX = 1;
        SplashSkipHint.Visibility = Visibility.Collapsed;
        SplashLoadingBar.IsIndeterminate = SystemParameters.ClientAreaAnimation;
        SplashLoadingBar.Visibility = SystemParameters.ClientAreaAnimation ? Visibility.Visible : Visibility.Collapsed;
        SplashLoading.Visibility = Visibility.Visible;
        ShowDashboardIfReady();
    }
    private void SkipSplash()
    {
        _introSound?.Stop();
        DismissSplash();
    }
    private void DismissSplash_Click(object sender, MouseButtonEventArgs e)
    {
        if (_recordingDelayTimer.IsEnabled) EndRecordingDelay();
        else SkipSplash();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (Splash.IsVisible)
        {
            if (e.Key == Key.Escape) SkipSplash();
            else if (!e.IsRepeat && e.Key is (Key.Enter or Key.Space))
            {
                if (_recordingDelayTimer.IsEnabled) EndRecordingDelay();
                else SkipSplash();
            }
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    private void LoadLibrary()
    {
        try
        {
            if (!File.Exists(_dataFile)) return;
            var entries = JsonSerializer.Deserialize<List<GameEntry>>(File.ReadAllText(_dataFile));
            if (entries is not null) _games.AddRange(entries);
        }
        catch { ShowToast("The saved library could not be read. Add your games again if needed."); }
    }

    private void SaveLibrary() => WriteJsonFile(_dataFile, _games);

    private static void WriteJsonFile<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(_settingsFile))
            {
                return;
            }
            var settings = JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(_settingsFile));
            _xvalkyriePath = settings?.XvalkyriePath;
            _startupSound = settings?.StartupSound ?? true;
            _showIntro = settings?.ShowIntro ?? true;
            _recordingDelay = settings?.RecordingDelay ?? false;
            _backgroundMotion = settings?.BackgroundMotion ?? true;
            _videoPreview = settings?.VideoPreview ?? true;
            _defaultLayout = settings?.DefaultLayout == "grid" ? "grid" : "carousel";
            _leonidaGameFolder = settings?.LeonidaGameFolder;
            // Older settings saved Series S by default, even when no profile had been chosen.
            _victoryConsoleMode = settings?.VictoryConsoleProfile == "series-s" ? "series-s" : "series-x";
            _gtaRuntime = settings?.GtaRuntime switch
            {
                "xcl" when !GtaOnlyEdition && FindXclRuntime() is not null => "xcl",
                { } runtime when VictoryBuilds.ContainsKey(runtime) && FindVictoryRuntime(runtime) is not null => runtime,
                _ => "victory"
            };
            _xclFrameDiagnostics = settings?.XclFrameDiagnostics ?? false;
            _xclPostRunPrecompile = settings?.XclPostRunPrecompile ?? true;
            if (_gtaRuntime == "xcl" && FindXclRuntime() is null) _gtaRuntime = "victory";
        }
        catch
        {
            _xvalkyriePath = null;
            if (FindXclRuntime() is null) _gtaRuntime = "victory";
        }
    }

    private void SaveSettings() => WriteJsonFile(_settingsFile, new LauncherSettings
    {
        XvalkyriePath = _xvalkyriePath, StartupSound = _startupSound,
        ShowIntro = _showIntro, RecordingDelay = _recordingDelay, BackgroundMotion = _backgroundMotion,
        VideoPreview = _videoPreview, DefaultLayout = _defaultLayout,
        LeonidaGameFolder = _leonidaGameFolder,
        VictoryConsoleProfile = _victoryConsoleMode,
        GtaRuntime = _gtaRuntime,
        XclFrameDiagnostics = _xclFrameDiagnostics,
        XclPostRunPrecompile = _xclPostRunPrecompile
    });

    private void RefreshRuntimeStatus()
    {
        PublishDashboardState();
    }

    private string? FindXvalkyrie()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "runtimes", "xvalkyrie", "xvalkyrie.exe");
        foreach (var candidate in new[] { _xvalkyriePath, local })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && Path.IsPathFullyQualified(candidate)
                && File.Exists(candidate) && File.Exists(Path.Combine(Path.GetDirectoryName(candidate)!, "xvboot.exe")))
                return candidate;
        }

        return null;
    }

    private static string LocalRuntimeFolder => Path.Combine(LauncherDataRoot, "runtimes");

    private static string? FindVictoryRuntime(string runtime = "victory")
    {
        var directory = VictoryBuilds[runtime].Directory;
        var installed = Path.Combine(LocalRuntimeFolder, directory, "victory.exe");
        if (File.Exists(installed)) return installed;
        var executable = Path.Combine(AppContext.BaseDirectory, "runtimes", directory, "victory.exe");
        return File.Exists(executable) ? executable : null;
    }

    private static string? FindXclRuntime()
    {
        foreach (var root in new[] { LocalRuntimeFolder, Path.Combine(AppContext.BaseDirectory, "runtimes") })
        {
            var directory = Path.Combine(root, "xcl");
            var executable = Path.Combine(directory, "xcl.exe");
            if (!File.Exists(executable)) continue;
            if (!File.Exists(Path.Combine(directory, "dxcompiler.dll")) ||
                !File.Exists(Path.Combine(directory, "dxil.dll"))) continue;
            return executable;
        }
        return null;
    }

    private bool IsXvalkyrieAvailable() => FindXvalkyrie() is not null;

    private void RefreshLibrary()
    {
        if (GameList is null) return;
        var query = SearchBox?.Text?.Trim() ?? "";
        IEnumerable<GameEntry> visible = _games;
        if (_recentOnly) visible = visible.Where(g => g.LastPlayedUtc is not null).OrderByDescending(g => g.LastPlayedUtc);
        else if (SortBox?.SelectedIndex == 1) visible = visible.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase);
        else visible = visible.OrderByDescending(g => g.AddedUtc);
        if (!string.IsNullOrWhiteSpace(query)) visible = visible.Where(g => g.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        var list = visible.ToList();
        GameList.ItemsSource = list;
        GameCount.Text = $"  {list.Count}";
        LibraryHeading.Text = _recentOnly ? "Recently played" : "Your games";
        UpdateFeatured(_featured is not null && list.Contains(_featured) ? _featured : null);
        PublishDashboardState();
        _ = PublishDashboardIconsAsync();
    }

    private void UpdateFeatured(GameEntry? game)
    {
        _featured = game;
        FeatureTitle.Text = game?.LibraryName ?? "Game library";
        FeatureAction.Content = game is null ? "Add Windows game" : "Launch";
        LibrarySubheading.Text = game is not null ? game.PlatformLabel
            : _games.Count == 0 ? "Add a Windows game or test the Xbox runtime."
            : $"{_games.Count} game{(_games.Count == 1 ? "" : "s")} in your library";
    }

    private void GameTile_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is GameEntry game) UpdateFeatured(game);
    }

    private async void FeatureAction_Click(object sender, RoutedEventArgs e)
    {
        if (_featured is { } game) await PlayGameAsync(game);
        else AddGame_Click(sender, e);
    }

    private void AddGame_Click(object sender, RoutedEventArgs e)
    {
        if (GtaOnlyEdition) return;
        var dialog = new OpenFileDialog { Title = "Choose a Windows game executable", Filter = "Windows applications (*.exe)|*.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        ImportGame(dialog.FileName, "Windows");
    }

    private void AddXboxTitle_Click(object sender, RoutedEventArgs e)
    {
        if (GtaOnlyEdition) return;
        if (!IsXvalkyrieAvailable())
        {
            ShowToast("Xbox compatibility files are not configured.");
            return;
        }
        var dialog = new OpenFileDialog { Title = "Choose an unpacked Xbox title executable", Filter = "Windows executable (*.exe)|*.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        ImportGame(dialog.FileName, "XValKyrie");
    }

    private void ImportGame(string path, string launchKind)
    {
        if (!string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            ShowToast("Choose a game executable (.exe).");
            return;
        }
        if (_games.Any(game => string.Equals(game.ExecutablePath, path, StringComparison.OrdinalIgnoreCase)))
        {
            ShowToast("That executable is already in your library.");
            return;
        }

        var stem = Path.GetFileNameWithoutExtension(path);
        var name = stem.Replace('_', ' ').Replace('-', ' ').Trim();
        var entry = new GameEntry
        {
            Name = name.Length == 0 ? stem : name,
            ExecutablePath = path,
            LaunchKind = launchKind,
            AddedUtc = DateTime.UtcNow
        };
        _games.Add(entry);
        try { SaveLibrary(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _games.Remove(entry);
            ShowToast($"Could not save the game to your library: {error.Message}");
            return;
        }

        _recentOnly = false;
        SearchBox.Clear();
        _featured = entry;
        RefreshLibrary();
        if (_dashboardReady) SendDashboardMessage(new { type = "game-added", id = path });
        ShowToast($"{entry.Name} added to your library.");
    }

    private async void PlayGame_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is GameEntry game) await PlayGameAsync(game);
    }

    private async Task PlayGameAsync(GameEntry game)
    {
        if (_prelaunchPreparing) { ShowToast("A game is already starting or running."); return; }
        if (game.IsLeonida)
        {
            try { await LaunchLeonidaAsync(game); }
            catch (Exception error) { ShowToast(error.Message); }
            return;
        }
        if (!File.Exists(game.ExecutablePath)) { ShowToast("Game file not found. Remove it and add the current file location."); return; }
        if (game.LaunchKind == "XValKyrie") { await PreflightAndLaunchXboxTitleAsync(game); return; }
        try
        {
            await LaunchTrackedGameAsync(game, new ProcessStartInfo
            {
                FileName = game.ExecutablePath,
                WorkingDirectory = Path.GetDirectoryName(game.ExecutablePath)!,
                Arguments = game.LaunchArguments ?? "",
                UseShellExecute = true
            });
        }
        catch (Exception ex) { ShowToast($"Could not start this game: {ex.Message}"); }
    }

    private async Task PreflightAndLaunchXboxTitleAsync(GameEntry game)
    {
        var runtimePath = FindXvalkyrie();
        if (runtimePath is null) { ShowToast("Xbox compatibility files are unavailable. Review the optional settings."); return; }
        var runtimeDirectory = Path.GetDirectoryName(runtimePath)!;
        var xvboot = Path.Combine(runtimeDirectory, "xvboot.exe");
        try
        {
            var assess = new ProcessStartInfo { FileName = xvboot, WorkingDirectory = Path.GetDirectoryName(xvboot)!, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            assess.ArgumentList.Add("assess");
            assess.ArgumentList.Add(game.ExecutablePath);
            using var process = Process.Start(assess);
            if (process is null) { ShowToast("Could not start the Xbox compatibility check."); return; }
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            var report = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
            if (process.ExitCode != 0)
            {
                MessageBox.Show(this, $"The Xbox compatibility check could not assess this executable (exit {process.ExitCode}).\n\n{LimitReport(report)}", "Nebula · Check failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var choice = MessageBox.Show(this,
                $"Xbox import assessment (informational only):\n\n{LimitReport(report)}\n\nThis does not confirm compatibility or playability. The selected title runs with your Windows account permissions. Continue?",
                "Nebula · Experimental title preflight", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (choice != MessageBoxResult.Yes) return;
            var runtime = new ProcessStartInfo { FileName = runtimePath, WorkingDirectory = runtimeDirectory, UseShellExecute = false };
            runtime.ArgumentList.Add(game.ExecutablePath);
            await LaunchTrackedGameAsync(game, runtime);
        }
        catch (Exception ex) { ShowToast($"Xbox compatibility check failed: {ex.Message}"); }
    }

    private async Task LaunchTrackedGameAsync(GameEntry game, ProcessStartInfo start)
    {
        if (_prelaunchPreparing) return;
        _prelaunchPreparing = true;
        try
        {
            SetGtaLaunchSplash("opening", "Waiting for the game window.", game.Name);
            using var process = Process.Start(start) ?? throw new IOException("The game did not start.");
            game.LastPlayedUtc = DateTime.UtcNow;
            SaveLibrary();
            RefreshLibrary();
            await TrackGameSessionAsync(process);
        }
        finally
        {
            _prelaunchPreparing = false;
            SetGtaLaunchSplash("closed");
        }
    }

    private static string LimitReport(string report)
    {
        const int maxLength = 2600;
        report = report.Trim();
        return report.Length <= maxLength ? report : report[..maxLength] + "\n… (report shortened)";
    }

    private void RemoveGame_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is GameEntry game) RemoveGame(game);
    }

    private void RemoveGame(GameEntry game)
    {
        if (GtaOnlyEdition) return;
        _executableIcons.Remove(game.ExecutablePath);
        _games.Remove(game); SaveLibrary(); RefreshLibrary(); ShowToast($"{game.Name} removed from the library.");
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshLibrary();
    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsInitialized) RefreshLibrary(); }
    private void LibraryNav_Click(object sender, RoutedEventArgs e) { _recentOnly = false; RefreshLibrary(); }
    private void RecentNav_Click(object sender, RoutedEventArgs e) { _recentOnly = true; RefreshLibrary(); }
    private void FocusSearch_Click(object sender, RoutedEventArgs e) { SearchBox.Focus(); SearchBox.SelectAll(); }
    private static bool DeveloperDiagnosticsAvailable => Directory.Exists(Path.Combine(AppContext.BaseDirectory, "diagnostics"));

    private void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (DeveloperDiagnosticsAvailable) new DiagnosticsWindow(FindXvalkyrie()) { Owner = this }.ShowDialog();
        else ShowToast("Developer diagnostics are available in workspace builds.");
    }
    private void Help_Click(object sender, RoutedEventArgs e)
        => BrowseRuntime();

    private void BrowseRuntime()
    {
        var dialog = new OpenFileDialog { Title = "Select an Xbox compatibility app", Filter = "Windows applications (*.exe)|*.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true)
        {
            if (!string.Equals(Path.GetFileName(dialog.FileName), "xvalkyrie.exe", StringComparison.OrdinalIgnoreCase)) { ShowToast("Choose the compatibility app, not its command-line helper."); return; }
            var xvboot = Path.Combine(Path.GetDirectoryName(dialog.FileName)!, "xvboot.exe");
            if (!File.Exists(xvboot)) { ShowToast("The selected folder is missing a required command-line helper."); return; }
            _xvalkyriePath = dialog.FileName; SaveSettings(); RefreshRuntimeStatus(); ShowToast("Xbox compatibility files linked. Title support is experimental."); return;
        }
    }
    private void ShowToast(string message)
    {
        if (SendDashboardToast(message)) return;
        ToastText.Text = message; Toast.Visibility = Visibility.Visible; _toastTimer.Stop(); _toastTimer.Start();
    }
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    public sealed class GameEntry
    {
        public string Name { get; set; } = "";
        public string? DisplayName { get; set; }
        public string? LaunchArguments { get; set; }
        public string ExecutablePath { get; set; } = "";
        public string LaunchKind { get; set; } = "Windows";
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsLeonida => LaunchKind == "Leonida";
        [System.Text.Json.Serialization.JsonIgnore]
        public string Id => IsLeonida ? "leonida:gtavi" : ExecutablePath;
        public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
        public DateTime? LastPlayedUtc { get; set; }
        public string ExecutableName => Path.GetFileName(ExecutablePath);
        [System.Text.Json.Serialization.JsonIgnore]
        public string LibraryName => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;
        public string PlatformLabel => LaunchKind switch
        {
            "Leonida" => "XBOX SERIES X",
            "XValKyrie" => "XBOX TITLE  ·  EXPERIMENTAL",
            _ => "WINDOWS PC  ·  READY TO PLAY"
        };
        public string Monogram => string.Concat(LibraryName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(s => char.ToUpperInvariant(s[0])));
        [System.Text.Json.Serialization.JsonIgnore]
        public Brush CoverBrush
        {
            get
            {
                var colors = new[] { ("#338C62", "#142E39"), ("#74599C", "#28334D"), ("#387C9A", "#1B3543"), ("#BD7846", "#47313A"), ("#A49C48", "#344539"), ("#A85568", "#382C43") };
                var index = (int)((uint)StringComparer.OrdinalIgnoreCase.GetHashCode(Name) % (uint)colors.Length);
                var gradient = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
                gradient.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(colors[index].Item1), 0));
                gradient.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(colors[index].Item2), 1));
                return gradient;
            }
        }
    }

    public sealed class LauncherSettings
    {
        public string? XvalkyriePath { get; set; }
        public bool StartupSound { get; set; } = true;
        public bool ShowIntro { get; set; } = true;
        public bool RecordingDelay { get; set; }
        public bool BackgroundMotion { get; set; } = true;
        public bool VideoPreview { get; set; } = true;
        public string DefaultLayout { get; set; } = "carousel";
        public string? LeonidaGameFolder { get; set; }
        public string? VictoryConsoleProfile { get; set; }
        public string GtaRuntime { get; set; } = "victory";
        public bool XclFrameDiagnostics { get; set; }
        public bool XclPostRunPrecompile { get; set; } = true;
    }
}
