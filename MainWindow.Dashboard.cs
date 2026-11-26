using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace XboxEmulatorUI;

public partial class MainWindow
{
    private const string DashboardUrl = "https://nexus.local/Dashboard/index.html";
    private WebView2? _dashboard;
    private bool _dashboardReady;
    private bool _dashboardPainted;
    private bool _dashboardFailed;
    private readonly ExecutableIcons _executableIcons = new();
    private readonly SteamCatalog _steam = new();
    private readonly GtaActivityFeed _gtaActivity = new();
    private CancellationTokenSource? _iconLoad;

    private async Task InitializeDashboardAsync()
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "Dashboard", "index.html")))
        {
            UseNativeDashboard("Dashboard files were not found.");
            return;
        }
        _ = WatchDashboardStartupAsync();
        var view = new WebView2
        {
            Visibility = Visibility.Hidden,
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(16, 16, 16)
        };
        _dashboard = view;
        LauncherRoot.Children.Add(view);
        System.Windows.Controls.Panel.SetZIndex(view, 20);
        try
        {
            var profile = Path.Combine(LauncherDataRoot, "Dashboard");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
            var controllerOptions = environment.CreateCoreWebView2ControllerOptions();
            try { controllerOptions.DefaultBackgroundColor = System.Drawing.Color.FromArgb(16, 16, 16); }
            catch (NotImplementedException) { }
            await view.EnsureCoreWebView2Async(environment, controllerOptions);
            var core = view.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping("nexus.local", AppContext.BaseDirectory,
                CoreWebView2HostResourceAccessKind.Deny);
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.NavigationStarting += (_, e) => e.Cancel = e.Uri != DashboardUrl;
            core.FrameNavigationStarting += (_, e) => e.Cancel = true;
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.WebMessageReceived += DashboardMessageReceived;
            core.NavigationCompleted += (_, e) =>
            {
                if (!e.IsSuccess) UseNativeDashboard($"Navigation failed: {e.WebErrorStatus}");
            };
            core.Navigate(DashboardUrl);
        }
        catch (Exception error)
        {
            UseNativeDashboard(error.Message);
            LauncherRoot.Children.Remove(view);
            view.Dispose();
            _dashboard = null;
        }
    }

    private async Task WatchDashboardStartupAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(15));
        if (!_dashboardPainted && IsLoaded) UseNativeDashboard("The dashboard did not finish loading.");
    }

    private void UseNativeDashboard(string reason)
    {
        if (_dashboardFailed) return;
        _dashboardFailed = true;
        _dashboardReady = false;
        _iconLoad?.Cancel();
        LogDashboard($"Native dashboard fallback: {reason}");
        ShowDashboardIfReady();
    }

    private void LogDashboard(string message)
    {
        try { File.AppendAllText(Path.Combine(Path.GetDirectoryName(_settingsFile)!, "dashboard.log"), $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void ShowDashboardIfReady()
    {
        if (!_introFinished || Splash.Visibility == Visibility.Collapsed ||
            (!_dashboardPainted && !_dashboardFailed)) return;
        if (_dashboardFailed)
        {
            NativeDashboard.Visibility = Visibility.Visible;
        }
        else if (_dashboard is { } view)
        {
            view.Visibility = Visibility.Visible;
            view.Focus();
        }
        Splash.Visibility = Visibility.Collapsed;
    }

    private void PublishDashboardState()
    {
        if (!_dashboardReady) return;
        SendDashboardMessage(new
        {
            type = "state",
            legalDisclaimerAccepted = _legalDisclaimerAccepted,
            launcherVersion = LauncherVersion,
            launcherUpdateUrl = LatestLauncherReleaseUrl,
            runtime = IsXvalkyrieAvailable(),
            startupSound = _startupSound,
            showIntro = _showIntro,
            recordingDelay = _recordingDelay,
            backgroundMotion = _backgroundMotion,
            videoPreview = _videoPreview,
            developerMode = _developerMode,
            prewarmShaders = _prewarmShaders,
            defaultLayout = _defaultLayout,
            victoryConsoleMode = _victoryConsoleMode,
            gtaRuntime = _gtaRuntime,
            gtaResolution = _gtaResolution,
            scarlettFoxRuntime = GtaOnlyEdition && IsScarlettFoxRuntime(_gtaRuntime),
            scarlettRayTracing = _scarlettRayTracing,
            gtaRuntimeFolderAvailable = FindGtaRuntime(_gtaRuntime) is not null,
            xclFrameDiagnostics = _xclFrameDiagnostics,
            xclPostRunPrecompile = _xclPostRunPrecompile,
            xclAvailable = FindXclRuntime() is not null,
            gtaOnlyEdition = GtaOnlyEdition,
            gitAvailable = GtaOnlyEdition && FindGitExecutable() is not null,
            rustAvailable = GtaOnlyEdition && FindCargoExecutable() is not null,
            victoryBuilds = GtaOnlyEdition ? VictoryBuilds.Select(item => new { id = item.Key, name = item.Value.Name, repository = item.Value.Repository,
                installed = FindVictoryRuntime(item.Key) is not null }) : null,
            customVictoryBuilds = GtaOnlyEdition ? GetCustomVictoryRuntimes().Select(runtime => new { id = runtime.Id, name = runtime.Name,
                repository = runtime.Repository, branch = runtime.Branch, installed = true }) : null,
            nvidiaVictoryAvailable = FindVictoryRuntime("victory-nvidia") is not null,
            victoryWorldAvailable = FindVictoryRuntime() is not null,
            victoryWorldInstalling = _victoryWorldInstalling,
            victoryWorldInstallStatus = _victoryWorldInstallStatus,
            victoryInstallBuildId = _victoryInstallBuildId,
            victoryInstallLatest = _victoryInstallLatest,
            diagnosticsAvailable = DeveloperDiagnosticsAvailable,
            xclStatus = GetXclStatus(),
            xclShaderBuild = _xclShaderBuildStatus,
            runtimePath = FindXvalkyrie(),
            customRuntime = !string.IsNullOrWhiteSpace(_xvalkyriePath),
            leonida = GetLeonidaState(),
            maximized = WindowState == WindowState.Maximized,
            games = _games.Select(game => new
            {
                id = game.Id,
                game.Name,
                game.DisplayName,
                game.Monogram,
                xbox = game.LaunchKind == "XValKyrie",
                leonida = game.IsLeonida,
                executableLinked = game.IsLeonida && File.Exists(game.ExecutablePath),
                added = game.AddedUtc,
                recent = game.LastPlayedUtc
            })
        });
    }

    private async Task PublishDashboardIconsAsync()
    {
        if (!_dashboardReady) return;
        _iconLoad?.Cancel();
        using var request = new CancellationTokenSource();
        _iconLoad = request;
        var paths = _games.Where(game => !game.IsLeonida).Select(game => game.ExecutablePath).ToArray();
        try
        {
            const int batchSize = 12;
            foreach (var batch in paths.Chunk(batchSize))
            {
                var results = await Task.Run(() => batch.Select(path =>
                {
                    request.Token.ThrowIfCancellationRequested();
                    return new { id = path, icon = _executableIcons.Read(path) };
                }).ToArray(), request.Token);
                if (request.IsCancellationRequested) return;
                SendDashboardMessage(new { type = "icons", results });
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception error) { LogDashboard($"Unable to read game icons: {error.Message}"); }
        finally { if (_iconLoad == request) _iconLoad = null; }
    }

    private bool SendDashboardToast(string message)
    {
        if (_dashboard?.Visibility != Visibility.Visible) return false;
        SendDashboardMessage(new { type = "toast", message });
        return true;
    }

    private void SendDashboardMessage(object message)
        => _dashboard?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message));

    private void DashboardMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_dashboardFailed || e.Source != DashboardUrl) return;
        try
        {
            using var message = JsonDocument.Parse(e.WebMessageAsJson);
            if (message.RootElement.ValueKind != JsonValueKind.Object ||
                !message.RootElement.TryGetProperty("action", out var action) ||
                action.ValueKind != JsonValueKind.String) return;
            string? id = null;
            if (message.RootElement.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.String)
                id = value.GetString();
            var command = action.GetString();
            var payload = message.RootElement.TryGetProperty("value", out var incoming) &&
                incoming.ValueKind == JsonValueKind.Object ? incoming.Clone() : default;
            // Modal WPF dialogs must open after WebView2's event callback returns.
            Dispatcher.BeginInvoke(new Action(() => _ = HandleDashboardCommandAsync(command, id, payload)));
        }
        catch (JsonException) { }
    }

    private async Task HandleDashboardCommandAsync(string? command, string? id, JsonElement payload)
    {
        if (GtaOnlyEdition && command is ("add-windows" or "add-xbox" or "browse-runtime" or "remove")) return;
        try
        {
            var args = new RoutedEventArgs();
            switch (command)
            {
                case "ready":
                    if (_dashboardReady) break;
                    _dashboardReady = true;
                    PublishDashboardState();
                    _ = CheckLauncherUpdateAsync();
                    await Task.WhenAny(PublishDashboardIconsAsync(), Task.Delay(1500));
                    if (!_dashboardFailed) SendDashboardMessage(new { type = "initial-library-ready" });
                    break;
                case "library-rendered":
                    if (!_dashboardReady || _dashboardPainted) break;
                    _dashboardPainted = true;
                    ShowDashboardIfReady();
                    LogDashboard("Local dashboard is ready.");
                    break;
                case "add-windows": AddGame_Click(this, args); break;
                case "add-xbox": AddXboxTitle_Click(this, args); break;
                case "diagnostics": Diagnostics_Click(this, args); break;
                case "browse-runtime": BrowseRuntime(); break;
                case "browse-leonida": BrowseLeonidaFolder(); break;
                case "clear-leonida": SetLeonidaFolder(null); break;
                case "leonida-project": OpenWebPage("https://github.com/ItsNotRexed/Leonida"); break;
                case "victory-project": OpenWebPage("https://github.com/ZSlayerHQ/victory-world-runtime/tree/next"); break;
                case "victory-nvidia-project": OpenWebPage("https://github.com/ad3lre/victory-nvidia-8gb"); break;
                case "save-developer-log": SaveDeveloperLog(payload); break;
                case "xcl-project": OpenWebPage("https://github.com/ivall/xcl/releases"); break;
                case "open-local-runtime-folder":
                    var xclFolder = Directory.CreateDirectory(Path.Combine(LocalRuntimeFolder, "xcl")).FullName;
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(xclFolder) { UseShellExecute = true })?.Dispose();
                    break;
                case "open-selected-runtime-folder":
                    var selectedRuntimeFile = FindGtaRuntime(_gtaRuntime)
                        ?? throw new FileNotFoundException("The selected GTA VI runtime is not installed.");
                    var selectedRuntimeFolder = Path.GetDirectoryName(selectedRuntimeFile)
                        ?? throw new DirectoryNotFoundException("The selected runtime folder could not be found.");
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(selectedRuntimeFolder) { UseShellExecute = true })?.Dispose();
                    break;
                case "reset-runtime":
                    _xvalkyriePath = null;
                    SaveSettings();
                    RefreshRuntimeStatus();
                    ShowToast("Using bundled runtime when available.");
                    break;
                case "save-preferences":
                    SaveDashboardPreferences(payload);
                    break;
                case "save-victory-graphics":
                    SaveVictoryGraphics(payload);
                    break;
                case "save-gta-resolution":
                    SaveGtaResolution(payload);
                    break;
                case "save-scarlett-ray-tracing":
                    SaveScarlettRayTracing(payload);
                    break;
                case "save-gta-runtime":
                    SaveGtaRuntime(payload);
                    break;
                case "install-victory-world":
                    await InstallVictoryWorldAsync(ReadOption(payload, "repository"));
                    break;
                case "install-victory-build" when GtaOnlyEdition:
                    await InstallVictoryBuildAsync(ReadOption(payload, "runtime"));
                    break;
                case "install-latest-victory-build" when GtaOnlyEdition:
                    await InstallVictoryBuildAsync(ReadOption(payload, "runtime"), latest: true);
                    break;
                case "install-custom-victory" when GtaOnlyEdition:
                    await InstallCustomVictoryAsync(ReadOption(payload, "repository"), ReadOption(payload, "branch"));
                    break;
                case "install-runtime-prerequisite" when GtaOnlyEdition:
                    await InstallRuntimePrerequisiteAsync(ReadOption(payload, "tool"));
                    break;
                case "runtime-security-continue":
                    _runtimeSecurityDecision?.TrySetResult(true);
                    break;
                case "runtime-security-cancel":
                    _runtimeSecurityDecision?.TrySetResult(false);
                    break;
                case "save-xcl-diagnostics":
                    SaveXclDiagnostics(payload);
                    break;
                case "save-xcl-postrun-precompile":
                    SaveXclPostRunPrecompile(payload);
                    break;
                case "build-xcl-shaders":
                    await BuildXclShaderCacheAsync();
                    break;
                case "cancel-xcl-shaders":
                    CancelXclShaderBuild();
                    break;
                case "cancel-launch-shaders":
                    _skipLaunchPrewarm = false;
                    if (_victoryPrewarmCancellation is not null) _victoryPrewarmCancellation.Cancel();
                    else CancelXclShaderBuild();
                    break;
                case "skip-launch-prewarm":
                    if (_prelaunchPreparing)
                    {
                        _skipLaunchPrewarm = true;
                        _victoryPrewarmCancellation?.Cancel();
                        _xclShaderBuildCts?.Cancel();
                    }
                    break;
                case "toggle-sound":
                    _startupSound = !_startupSound;
                    SaveSettings();
                    PublishDashboardState();
                    ShowToast(_startupSound ? "Startup sound on" : "Startup sound muted");
                    break;
                case "minimize": WindowState = WindowState.Minimized; break;
                case "maximize":
                    WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                    PublishDashboardState();
                    break;
                case "close": Close(); break;
                case "accept-legal-disclaimer" when GtaOnlyEdition:
                    _legalDisclaimerAccepted = true;
                    SaveSettings();
                    PublishDashboardState();
                    break;
                case "decline-legal-disclaimer" when GtaOnlyEdition:
                    Close();
                    break;
                case "open-launcher-update" when GtaOnlyEdition:
                    OpenWebPage(LatestLauncherReleaseUrl);
                    break;
                case "drag":
                    if (WindowState == WindowState.Normal)
                    {
                        ReleaseCapture();
                        SendMessage(new WindowInteropHelper(this).Handle, 0x00A1, 2, 0);
                    }
                    break;
                case "catalog":
                case "activity":
                case "media":
                case "steam-store":
                case "twitch":
                case "youtube":
                case "google-reviews":
                case "gtavi-media":
                case "gtavi-activity":
                case "gtavi-article":
                case "steam-reviews":
                case "steam-news":
                case "play":
                case "remove":
                case "game-properties":
                case "save-game-properties":
                case "open-game-folder":
                    var game = _games.Find(entry => string.Equals(entry.Id, id, StringComparison.Ordinal));
                    if (game is null) return;
                    if (command == "gtavi-activity" && game.IsLeonida)
                    {
                        var feed = await _gtaActivity.GetAsync();
                        if (_games.Contains(game)) SendDashboardMessage(new { type = "gtavi-activity", id, feed });
                    }
                    else if (command == "gtavi-article" && game.IsLeonida)
                    {
                        var url = payload.ValueKind == JsonValueKind.Object ? ReadOption(payload, "url") : "";
                        if (_gtaActivity.CanOpen(url)) OpenWebPage(url);
                    }
                    else if (command == "game-properties") PublishGameProperties(game);
                    else if (command == "save-game-properties") SaveGameProperties(game, payload);
                    else if (command == "open-game-folder") OpenGameFolder(game);
                    else if (command == "play") await PlayGameAsync(game);
                    else if (command == "remove") RemoveGame(game);
                    else if (command == "catalog")
                    {
                        var card = await _steam.FindAsync(game.Name, game.ExecutablePath);
                        if (_games.Contains(game))
                            SendDashboardMessage(new { type = "catalog", id, card });
                    }
                    else if (command == "activity")
                    {
                        var card = await _steam.FindAsync(game.Name, game.ExecutablePath);
                        var activity = card is null ? null : await _steam.FindActivityAsync(card.AppId);
                        if (_games.Contains(game))
                            SendDashboardMessage(new { type = "activity", id, activity });
                    }
                    else if (command == "media")
                    {
                        var card = await _steam.FindAsync(game.Name, game.ExecutablePath);
                        var media = card is null ? null : await _steam.FindMediaAsync(card.AppId);
                        if (_games.Contains(game))
                            SendDashboardMessage(new { type = "media", id, media });
                    }
                    else await OpenDiscoveryAsync(command, game);
                    break;
            }
        }
        catch (Exception error) { ShowToast(error.Message); }
    }

    private async Task OpenDiscoveryAsync(string command, GameEntry game)
    {
        var term = Uri.EscapeDataString(game.Name);
        var url = command switch
        {
            "twitch" => $"https://www.twitch.tv/search?term={term}",
            "youtube" => $"https://www.youtube.com/results?search_query={term}%20live%20gameplay",
            "google-reviews" => $"https://www.google.com/search?q={term}%20game%20reviews",
            "gtavi-media" when game.IsLeonida => "https://www.rockstargames.com/VI/media/videos",
            _ => null
        };
        if (command is "steam-reviews" or "steam-news" or "steam-store")
        {
            var card = await _steam.FindAsync(game.Name, game.ExecutablePath);
            if (card is not null)
                url = command switch
                {
                    "steam-news" => $"https://store.steampowered.com/news/app/{card.AppId}",
                    "steam-reviews" => $"https://store.steampowered.com/app/{card.AppId}/#app_reviews_hash",
                    _ => $"https://store.steampowered.com/app/{card.AppId}/"
                };
        }
        if (url is not null) OpenWebPage(url);
    }

    private static void OpenWebPage(string url)
        => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();

    protected override void OnClosed(EventArgs e)
    {
        _leonidaAvailabilityTimer.Stop();
        _recordingDelayTimer.Stop();
        _iconLoad?.Cancel();
        _introSound?.Dispose();
        _dashboard?.Dispose();
        _dashboard = null;
        base.OnClosed(e);
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint handle, uint message, nint wParam, nint lParam);
}
