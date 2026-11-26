using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Win32;

namespace XboxEmulatorUI;

public partial class MainWindow
{
    private string? _leonidaGameFolder;
    private const string LeonidaExecutableName = "game_scarlett_master.exe";
    private string _victoryConsoleMode = "series-x";
    private static readonly Semaphore ShaderPrebuildGate = new(1, 1, @"Local\NexusLauncherShaderPrebuild");
    private CancellationTokenSource? _xclShaderBuildCts;
    private XclShaderBuildStatus _xclShaderBuildStatus = new("idle", 0, 0, "");
    private Process? _xclLaunchPrecompileProcess;
    private bool _prelaunchPreparing;

    private sealed record XclShaderBuildStatus(string Status, int Progress, int Total, string Message);

    private void SaveXclDiagnostics(JsonElement value)
    {
        var enabled = ReadPreference(value, "frameDiagnostics", _xclFrameDiagnostics);
        var previous = _xclFrameDiagnostics;
        _xclFrameDiagnostics = enabled;
        try { SaveSettings(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _xclFrameDiagnostics = previous;
            throw;
        }
        PublishDashboardState();
        ShowToast(enabled ? "XCL frame sampling enabled · small performance cost." : "XCL frame sampling disabled for normal performance.");
    }

    private void SaveXclPostRunPrecompile(JsonElement value)
    {
        var enabled = ReadPreference(value, "enabled", _xclPostRunPrecompile);
        var previous = _xclPostRunPrecompile;
        _xclPostRunPrecompile = enabled;
        try { SaveSettings(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _xclPostRunPrecompile = previous;
            throw;
        }
        PublishDashboardState();
        ShowToast(enabled ? "XCL cache warm-up after the game exits is enabled." : "Automatic XCL cache warm-up is disabled.");
    }

    private void CancelXclShaderBuild()
    {
        if (_xclShaderBuildCts is null && _xclLaunchPrecompileProcess is null) return;
        _xclShaderBuildCts?.Cancel();
        try { if (_xclLaunchPrecompileProcess is { HasExited: false } process) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception error) { ShowToast($"Could not stop XCL cache warm-up: {error.Message}"); }
        ShowToast("Cancelling XCL shader prebuild…");
    }

    private async Task BuildXclShaderCacheAsync(string? packageFolder = null)
    {
        if (_xclShaderBuildCts is not null)
        {
            if (packageFolder is not null) throw new InvalidOperationException("An XCL shader build is already running.");
            return;
        }
        var executable = FindXclRuntime() ?? throw new FileNotFoundException("XCL is not included in this launcher build.");
        var folder = packageFolder ?? _leonidaGameFolder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            throw new DirectoryNotFoundException("Select the GTA VI package folder before building its XCL shader cache.");
        var shaderPack = Path.Combine(folder, "shaders.rpf");
        if (!File.Exists(shaderPack)) throw new FileNotFoundException("The selected package does not contain shaders.rpf.", shaderPack);
        if (!ShaderPrebuildGate.WaitOne(0))
        {
            _xclShaderBuildStatus = new("busy", 0, 0, "Another shader cache build is already running. Let it finish before starting XCL.");
            SendDashboardMessage(new { type = "xcl-shader-build", status = _xclShaderBuildStatus });
            ShowToast("Another shader cache build is already running.");
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _xclShaderBuildCts = cancellation;
        var total = 0;
        var done = 0;
        void Update(string message)
        {
            _xclShaderBuildStatus = new("running", done, total, message);
            SendDashboardMessage(new { type = "xcl-shader-build", status = _xclShaderBuildStatus });
        }

        try
        {
            var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var xclRoot = Path.Combine(localData, "NexusLauncher", "XCL");
            var cache = Path.Combine(xclRoot, "cache", "dxil");
            Directory.CreateDirectory(cache);
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.Environment["XCL_DATA_ROOT"] = xclRoot;
            start.Environment["XCL_DXC_DIR"] = Path.GetDirectoryName(executable)!;
            start.Environment["XCL_DXIL_CACHE"] = cache;
            start.Environment["XCL_DXC_THREADS"] = "2";
            start.Environment["XCL_PIPELINE_THREADS"] = "1";
            start.Environment["XCL_TRANSLATED_CACHE"] = "1";
            start.ArgumentList.Add("shaders");
            start.ArgumentList.Add(shaderPack);
            start.ArgumentList.Add("--precompile");
            // Fill the DXIL cache without creating GPU compute PSOs while a game may be using the adapter.
            start.ArgumentList.Add("--windows");
            start.ArgumentList.Add("4");

            using var process = Process.Start(start) ?? throw new IOException("XCL shader precompiler could not start.");
            Update("Scanning shaders.rpf and warming the persistent DXIL and compute-pipeline cache…");
            async Task ReadProgressAsync(StreamReader reader)
            {
                while (await reader.ReadLineAsync() is { } line)
                {
                    var text = line.Trim();
                    if (text.StartsWith("precompiling ", StringComparison.OrdinalIgnoreCase))
                    {
                        var number = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1);
                        if (int.TryParse(number, out var count)) total = count;
                        Update($"Prebuilding {total:N0} shader records into the persistent XCL cache…");
                    }
                    else
                    {
                        var fraction = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Split('/');
                        if (fraction is { Length: 2 } && int.TryParse(fraction[0], out var current) && int.TryParse(fraction[1], out var reportedTotal))
                        {
                            done = current;
                            if (reportedTotal > 0) total = reportedTotal;
                            Update($"Prebuilding XCL shaders · {done:N0} / {total:N0} records");
                        }
                        else if (text.StartsWith("pipelines:", StringComparison.OrdinalIgnoreCase))
                            Update(text);
                    }
                }
            }
            var stdout = ReadProgressAsync(process.StandardOutput);
            var stderr = ReadProgressAsync(process.StandardError);
            try { await process.WaitForExitAsync(cancellation.Token); }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync();
            }
            await Task.WhenAll(stdout, stderr);
            if (cancellation.IsCancellationRequested)
                _xclShaderBuildStatus = new("cancelled", done, total, "XCL shader prebuild cancelled. Completed cache entries are retained and will be reused.");
            else if (process.ExitCode == 0)
                _xclShaderBuildStatus = new("complete", total, total, $"XCL shader cache build completed. Cache: {cache}");
            else
                _xclShaderBuildStatus = new("error", done, total, $"XCL prebuild exited with code {process.ExitCode}. See the latest XCL report and logs.");
            SendDashboardMessage(new { type = "xcl-shader-build", status = _xclShaderBuildStatus });
            if (packageFolder is null) ShowToast(_xclShaderBuildStatus.Message);
        }
        finally
        {
            _xclShaderBuildCts = null;
            ShaderPrebuildGate.Release();
        }
    }

    private void SaveVictoryGraphics(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return;
        var mode = ReadOption(value, "consoleMode");
        if (mode is not ("series-s" or "series-x")) return;
        var previous = _victoryConsoleMode;
        _victoryConsoleMode = mode;
        try { SaveSettings(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _victoryConsoleMode = previous;
            throw;
        }
        PublishDashboardState();
        ShowToast("GTA VI graphics profile saved for the next launch.");
    }

    private const string NvidiaVictoryLabel = "Nvidia 8g";
    private const string NvidiaAccountPickerHint = "Press Enter or controller A when Account Picker appears in the bottom-right corner.";

    private static string GtaRuntimeLabel(string runtime) => runtime == "xcl" ? "XCL" : VictoryBuilds[runtime].Name;

    private static string? FindGtaRuntime(string runtime) => runtime == "xcl"
        ? FindXclRuntime() : FindVictoryRuntime(runtime);

    private void SaveGtaRuntime(JsonElement value)
    {
        var runtime = ReadOption(value, "runtime");
        if (!VictoryBuilds.ContainsKey(runtime) && runtime != "xcl") return;
        if (GtaOnlyEdition ? runtime == "xcl" : runtime == "victory-amd") return;
        var label = GtaRuntimeLabel(runtime);
        if (FindGtaRuntime(runtime) is null)
            throw new FileNotFoundException($"{label} is unavailable in this launcher build.");
        var previous = _gtaRuntime;
        _gtaRuntime = runtime;
        try { SaveSettings(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _gtaRuntime = previous;
            throw;
        }
        PublishDashboardState();
        ShowToast($"GTA VI will launch with {label}.");
    }

    private async Task LaunchLeonidaAsync(GameEntry game)
    {
        if (!File.Exists(game.ExecutablePath)) { BrowseLeonidaExecutable(game); return; }
        if (_prelaunchPreparing) { ShowToast("GTA VI is already starting or running."); return; }
        var useXcl = _gtaRuntime == "xcl";
        var nvidia = _gtaRuntime == "victory-nvidia";
        var selectedRuntime = _gtaRuntime;
        var runtime = GtaRuntimeLabel(_gtaRuntime);
        var executable = FindGtaRuntime(_gtaRuntime)
            ?? throw new FileNotFoundException($"{runtime} is unavailable. Check GTA VI runtime settings.");
        _prelaunchPreparing = true;
        try
        {
            if (useXcl)
            {
                var folder = Path.GetDirectoryName(game.ExecutablePath)!;
                var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NexusLauncher", "XCL", "cache", "dxil");
                var hasCache = Directory.Exists(cache) && Directory.EnumerateFiles(cache, "*", SearchOption.AllDirectories).Any();
                if (!hasCache && File.Exists(Path.Combine(folder, "shaders.rpf")))
                {
                    SetGtaLaunchSplash("shaders", "Scanning the game’s shader archive…");
                    await BuildXclShaderCacheAsync(folder);
                    if (_xclShaderBuildStatus.Status != "complete")
                    {
                        ShowToast(_xclShaderBuildStatus.Message);
                        return;
                    }
                }
            }
            else if (!await PrewarmVictoryAsync(executable, game.ExecutablePath, selectedRuntime))
                return;

            var openingMessage = useXcl
                ? "Waiting for the game window. Shaders may compile as you play."
                : nvidia ? NvidiaAccountPickerHint : "The game window may stay black while it reads the package.";
            SetGtaLaunchSplash("opening", openingMessage, runtimeLabel: nvidia ? NvidiaVictoryLabel : null);
            if (useXcl) await LaunchXclAsync(executable, game);
            else await LaunchVictoryAsync(executable, game.ExecutablePath, selectedRuntime);
        }
        finally
        {
            _prelaunchPreparing = false;
            SetGtaLaunchSplash("closed");
        }
    }

    private void SetGtaLaunchSplash(string phase, string? message = null, string? title = null, int elapsed = 0, string? runtimeLabel = null)
        => SendDashboardMessage(new { type = "gta-launch-splash", phase, message, title, elapsed, runtimeLabel });

    private async Task TrackGameSessionAsync(Process process, string? runningMessage = null)
    {
        var exit = process.WaitForExitAsync();
        var elapsed = Stopwatch.StartNew();
        while (!exit.IsCompleted)
        {
            process.Refresh();
            if (process.HasExited) break;
            if (process.MainWindowHandle != IntPtr.Zero)
            {
                SetGtaLaunchSplash("running", runningMessage);
                break;
            }
            SetGtaLaunchSplash("waiting", elapsed: (int)elapsed.Elapsed.TotalSeconds);
            await Task.WhenAny(exit, Task.Delay(1000));
        }
        await exit;
        SetGtaLaunchSplash("closed");
    }

    private async Task LaunchXclAsync(string executable, GameEntry game)
    {
        var gameExecutable = game.ExecutablePath;
        var xclDirectory = Path.GetDirectoryName(executable)!;
        var compiler = Path.Combine(xclDirectory, "dxcompiler.dll");
        var validator = Path.Combine(xclDirectory, "dxil.dll");
        if (!File.Exists(compiler) || !File.Exists(validator))
            throw new FileNotFoundException("XCL's bundled DirectX Shader Compiler files are incomplete. Rebuild the launcher package.", compiler);

        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var xclRoot = Path.Combine(localData, "NexusLauncher", "XCL");
        var cacheRoot = Path.Combine(xclRoot, "cache");
        var dxilCache = Path.Combine(cacheRoot, "dxil");
        Directory.CreateDirectory(dxilCache);
        var logDirectory = Path.Combine(xclRoot, "launcher-logs");
        Directory.CreateDirectory(logDirectory);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var stdoutPath = Path.Combine(logDirectory, $"xcl-{stamp}.stdout.log");
        var stderrPath = Path.Combine(logDirectory, $"xcl-{stamp}.stderr.log");
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = xclDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.Environment["XCL_DATA_ROOT"] = xclRoot;
        start.Environment["XCL_DXC_DIR"] = xclDirectory;
        start.Environment["XCL_DXIL_CACHE"] = dxilCache;
        start.Environment["XCL_DXC_THREADS"] = "2";
        start.Environment["XCL_PIPELINE_THREADS"] = "1";
        start.Environment["XCL_TRANSLATED_CACHE"] = "1";
        // Frame readback is optional because even sparse 4K captures cost CPU and disk bandwidth.
        start.Environment.Remove("XCL_FRAME_DUMP");
        start.Environment.Remove("XCL_FRAME_DUMP_FULL");
        if (_xclFrameDiagnostics) start.Environment["XCL_FRAME_DUMP"] = "120";
        start.ArgumentList.Add("run");
        start.ArgumentList.Add(gameExecutable);
        using var process = Process.Start(start) ?? throw new IOException("XCL could not start.");
        game.LastPlayedUtc = DateTime.UtcNow;
        SaveLibrary();
        RefreshLibrary();


        var stdout = DrainRuntimeOutputAsync(process.StandardOutput, stdoutPath);
        var stderr = DrainRuntimeOutputAsync(process.StandardError, stderrPath);
        await TrackGameSessionAsync(process);
        await Task.WhenAll(stdout, stderr);
        if (_xclPostRunPrecompile && File.Exists(Path.Combine(Path.GetDirectoryName(gameExecutable)!, "shaders.rpf")))
            await StartXclLaunchPrecompileAsync(executable, gameExecutable, xclDirectory, xclRoot, dxilCache);
        PublishDashboardState();
        if (IsLoaded)
            ShowToast($"XCL exited with code {process.ExitCode}. Logs: {stderrPath}");
    }

    private async Task StartXclLaunchPrecompileAsync(string executable, string gameExecutable, string xclDirectory, string xclRoot, string dxilCache)
    {
        if (_xclLaunchPrecompileProcess is { HasExited: false }) return;
        if (!ShaderPrebuildGate.WaitOne(0))
        {
            ShowToast("XCL is using its saved shader cache. A background prebuild is already active.");
            return;
        }

        var shaderPack = Path.Combine(Path.GetDirectoryName(gameExecutable)!, "shaders.rpf");
        var logDirectory = Path.Combine(xclRoot, "launcher-logs");
        Directory.CreateDirectory(logDirectory);
        var logPath = Path.Combine(logDirectory, $"xcl-precompile-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        try
        {
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = xclDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.Environment["XCL_DATA_ROOT"] = xclRoot;
            start.Environment["XCL_DXC_DIR"] = xclDirectory;
            start.Environment["XCL_DXIL_CACHE"] = dxilCache;
            start.Environment["XCL_DXC_THREADS"] = "2";
            start.Environment["XCL_PIPELINE_THREADS"] = "1";
            start.Environment["XCL_TRANSLATED_CACHE"] = "1";
            start.ArgumentList.Add("shaders");
            start.ArgumentList.Add(shaderPack);
            start.ArgumentList.Add("--precompile");
            start.ArgumentList.Add("--windows");
            start.ArgumentList.Add("4");

            var process = Process.Start(start);
            if (process is null) throw new IOException("XCL background shader precompile could not start.");
            _xclLaunchPrecompileProcess = process;
            _xclShaderBuildCts = new CancellationTokenSource();
            var stdoutTask = DrainRuntimeOutputAsync(process.StandardOutput, logPath);
            var stderrTask = DrainRuntimeOutputAsync(process.StandardError, logPath + ".stderr");
            _xclShaderBuildStatus = new("running", 0, 0, "XCL is warming missing DXIL cache entries after game exit; the game did not wait for this pass.");
            SendDashboardMessage(new { type = "xcl-shader-build", status = _xclShaderBuildStatus });
            try { await process.WaitForExitAsync(_xclShaderBuildCts.Token); }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync();
            }
            await Task.WhenAll(stdoutTask, stderrTask);
            _xclShaderBuildStatus = _xclShaderBuildCts.IsCancellationRequested
                ? new("cancelled", 0, 0, $"XCL post-run cache warm-up cancelled; completed DXIL cache entries remain reusable. Log: {logPath}")
                : process.ExitCode == 0
                ? new("complete", 0, 0, $"XCL background DXIL cache warm-up finished. Log: {logPath}")
                : new("error", 0, 0, $"XCL background cache warm-up exited with {process.ExitCode}. Log: {logPath}");
            SendDashboardMessage(new { type = "xcl-shader-build", status = _xclShaderBuildStatus });
        }
        catch (Exception error)
        {
            _xclShaderBuildStatus = new("error", 0, 0, $"XCL background cache warm-up could not start: {error.Message}");
            SendDashboardMessage(new { type = "xcl-shader-build", status = _xclShaderBuildStatus });
        }
        finally
        {
            _xclLaunchPrecompileProcess?.Dispose();
            _xclLaunchPrecompileProcess = null;
            _xclShaderBuildCts?.Dispose();
            _xclShaderBuildCts = null;
            ShaderPrebuildGate.Release();
        }
    }

    private static object GetXclStatus()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexusLauncher", "XCL");
        var reportPath = Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "report.json", SearchOption.AllDirectories)
                .OrderByDescending(path => File.GetLastWriteTimeUtc(path)).FirstOrDefault()
            : null;
        if (reportPath is null) return new { available = false, message = "No XCL run report yet." };
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(reportPath));
            var rootElement = json.RootElement;
            var gpu = rootElement.TryGetProperty("d3d12", out var d3d12) ? d3d12 : default;
            var shaders = rootElement.TryGetProperty("shaders", out var shaderInfo) ? shaderInfo : default;
            var cache = gpu.ValueKind == JsonValueKind.Object && gpu.TryGetProperty("dxil_cache", out var cacheInfo) ? cacheInfo : default;
            var frame = gpu.ValueKind == JsonValueKind.Object && gpu.TryGetProperty("last_frame_dump", out var frameInfo) ? frameInfo : default;
            var errors = gpu.ValueKind == JsonValueKind.Object && gpu.TryGetProperty("translation_errors", out var errorInfo) && errorInfo.ValueKind == JsonValueKind.Array
                ? errorInfo.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).Distinct(StringComparer.Ordinal).ToArray()
                : [];
            long Number(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;
            string? String(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            return new
            {
                available = true,
                reportPath,
                updated = File.GetLastWriteTime(reportPath).ToString("g"),
                gpu = String(gpu, "adapter"),
                presents = Number(gpu, "presents"),
                shaderBlobs = Number(shaders, "unique"),
                translatedPipelines = Number(gpu, "translated_pipelines"),
                dxcCompilations = Number(cache, "compilations"),
                sampledFrame = Number(frame, "frame"),
                nonDarkSamples = Number(frame, "non_dark_samples"),
                cacheHits = Number(cache, "hits"),
                cacheMisses = Number(cache, "misses"),
                failedPipelines = Number(gpu, "failed_pipelines"),
                translationErrorCount = errors.Length,
                translationErrors = errors.Take(4).ToArray(),
                crash = String(rootElement, "crash"),
                exitCode = rootElement.TryGetProperty("exit_code", out var exit) && exit.ValueKind == JsonValueKind.Number ? exit.GetInt64().ToString() : null
            };
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return new { available = false, reportPath, message = $"Could not read the latest XCL report: {error.Message}" };
        }
    }

    private static async Task DrainRuntimeOutputAsync(StreamReader reader, string path)
    {
        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        await reader.BaseStream.CopyToAsync(output);
    }

    private async Task LaunchVictoryAsync(string executable, string gameExecutable, string runtime)
    {
        var nvidia = runtime == "victory-nvidia";
        var victoryRoot = Path.Combine(LauncherDataRoot, "VIctory");
        var logDirectory = Path.Combine(victoryRoot, "launcher-logs");
        Directory.CreateDirectory(logDirectory);
        var logName = $"{runtime}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        var start = CreateVictoryStart(executable, gameExecutable, runtime, out _);
        start.Environment["VICTORY_LOG"] = "victory_loader=warn,victory_gpu::host=info,victory_gpu::frame=info,victory_gpu=warn";
        if (nvidia)
        {
            start.Environment.TryAdd("VICTORY_PIPELINE_WORKERS", "2");
            start.Environment["VICTORY_LOG"] += ",victory_gpu::pipeline::make=info";
        }
        // Avoid blocking the render thread on texture remaps and batch GPU buffer hashes.
        if (!nvidia)
        {
            start.Environment.TryAdd("VICTORY_TEXTURE_NO_WAIT", "1");
            start.Environment.TryAdd("VICTORY_CARD_REHASH_RUNS", "1");
        }
        start.ArgumentList.Add("run");
        start.ArgumentList.Add(gameExecutable);
        start.ArgumentList.Add("--console");
        start.ArgumentList.Add(_victoryConsoleMode);
        start.ArgumentList.Add("--execute");
        using var process = Process.Start(start) ?? throw new IOException("VIctory could not start.");
        var stderrPath = Path.Combine(logDirectory, $"{logName}.stderr.log");
        await Task.WhenAll(
            TrackGameSessionAsync(process, nvidia ? NvidiaAccountPickerHint : null),
            DrainRuntimeOutputAsync(process.StandardOutput, Path.Combine(logDirectory, $"{logName}.stdout.log")),
            DrainRuntimeOutputAsync(process.StandardError, stderrPath));
        if (IsLoaded)
        {
            IEnumerable<string> errors = process.ExitCode == 0 ? [] : File.ReadLines(stderrPath);
            if (errors.Any(line => line.Contains("LLVM ERROR: out of memory", StringComparison.Ordinal)))
                ShowToast("The graphics compiler ran out of memory while building pipelines. See the latest launch log.");
            else if (errors.Any(line => line.Contains("Windows commit limit reached", StringComparison.Ordinal)))
                ShowToast("Windows ran out of committed memory. Close other memory-heavy apps and retry.");
            else if (process.ExitCode == unchecked((int)0x80000003))
                ShowToast("GTA VI stopped at an internal breakpoint (0x80000003). See the latest launch log.");
            else
                ShowToast($"VIctory exited with code {process.ExitCode}.");
        }
    }

    private static string? ReadLeonidaFaultSummary(string trace)
    {
        try
        {
            foreach (var line in File.ReadLines(trace).Reverse())
            {
                using var record = JsonDocument.Parse(line);
                var root = record.RootElement;
                if (!root.TryGetProperty("kind", out var kind) || kind.GetString() != "exception") continue;
                var at = root.TryGetProperty("at", out var location) ? location.GetString() : null;
                var access = root.TryGetProperty("access", out var operation) ? operation.GetString() : null;
                var address = root.TryGetProperty("address", out var target) ? target.GetString() : null;
                var details = string.Join(" ", new[] { access, address }.Where(value => !string.IsNullOrWhiteSpace(value)));
                return string.IsNullOrWhiteSpace(at) ? details : string.IsNullOrWhiteSpace(details) ? at : $"{at}, {details}";
            }
        }
        catch (IOException) { }
        catch (JsonException) { }
        return null;
    }
    private readonly DispatcherTimer _leonidaAvailabilityTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private (string? Candidate, bool Linked) _lastLeonidaAvailability;

    private string? GetLeonidaCandidate()
    {
        if (string.IsNullOrWhiteSpace(_leonidaGameFolder)) return null;
        var path = Path.Combine(_leonidaGameFolder, LeonidaExecutableName);
        return File.Exists(path) ? path : null;
    }

    private void RefreshLeonidaAvailability()
    {
        var game = _games.Find(entry => entry.IsLeonida);
        var availability = game is null ? default : (GetLeonidaCandidate(), File.Exists(game.ExecutablePath));
        if (availability == _lastLeonidaAvailability) return;
        _lastLeonidaAvailability = availability;
        PublishDashboardState();
    }

    private void BrowseLeonidaExecutable(GameEntry game)
    {
        var picker = new OpenFileDialog
        {
            Title = "Select the GTA VI executable",
            Filter = "GTA VI Xbox Series executable|game_scarlett_master.exe",
            FileName = LeonidaExecutableName,
            CheckFileExists = true
        };
        if (Directory.Exists(_leonidaGameFolder)) picker.InitialDirectory = _leonidaGameFolder;
        if (picker.ShowDialog(this) != true) return;
        if (!string.Equals(Path.GetFileName(picker.FileName), LeonidaExecutableName, StringComparison.OrdinalIgnoreCase))
        {
            ShowToast($"Select {LeonidaExecutableName} from your GTA VI package.");
            return;
        }

        var previous = game.ExecutablePath;
        game.ExecutablePath = picker.FileName;
        try { SaveLibrary(); }
        catch
        {
            game.ExecutablePath = previous;
            throw;
        }
        RefreshLibrary();
        ShowToast("GTA VI executable linked. Compatibility has not completed a verified game boot.");
    }

    private object GetLeonidaState()
    {
        var selected = !string.IsNullOrWhiteSpace(_leonidaGameFolder);
        var exists = selected && Directory.Exists(_leonidaGameFolder);
        var candidate = GetLeonidaCandidate();
        var hasEncrypted = exists && File.Exists(Path.Combine(_leonidaGameFolder!, "game_scarlett_master.exe.encrypted.bin"));
        return new
        {
            folder = _leonidaGameFolder,
            exists,
            executable = candidate is not null,
            candidate,
            encrypted = hasEncrypted
        };
    }

    private void BrowseLeonidaFolder()
    {
        var picker = new OpenFolderDialog { Title = "Choose your GTA VI Xbox Series package folder" };
        if (Directory.Exists(_leonidaGameFolder)) picker.InitialDirectory = _leonidaGameFolder;
        if (picker.ShowDialog(this) == true) SetLeonidaFolder(picker.FolderName);
    }

    private void SetLeonidaFolder(string? folder)
    {
        var previous = _leonidaGameFolder;
        _leonidaGameFolder = folder;
        try { SaveSettings(); }
        catch
        {
            _leonidaGameFolder = previous;
            throw;
        }
        if (GtaOnlyEdition)
        {
            _games[0].ExecutablePath = GetLeonidaCandidate() ?? "";
            RefreshLibrary();
        }
        else PublishDashboardState();
        ShowToast(folder is null ? "GTA VI folder cleared." : "GTA VI folder saved.");
    }
}
