using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace XboxEmulatorUI;

public partial class MainWindow
{
    private enum VictoryPrewarmResult { Ready, CancelLaunch, Skipped }
    private CancellationTokenSource? _victoryPrewarmCancellation;
    private bool _skipLaunchPrewarm;

    private ProcessStartInfo CreateVictoryStart(string executable, string gameExecutable, string runtime, out string cache)
    {
        var nvidia = runtime == "victory-nvidia";
        var root = Path.Combine(LauncherDataRoot, "VIctory");
        cache = Path.Combine(root, runtime switch
        {
            "victory-nvidia" => "nvidia-8gb-cache",
            TrustNvidiaRuntime => "trustno1-nvidia-cache",
            "victory-amd" => "amd-cache",
            _ when runtime.StartsWith("custom-", StringComparison.Ordinal) => Path.Combine("custom", runtime),
            _ => "world-cache"
        });
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.Environment["VICTORY_GAME_DIR"] = Path.GetDirectoryName(gameExecutable)!;
        start.Environment["VICTORY_CACHE_DIR"] = cache;
        start.Environment["VICTORY_DATA_DIR"] = Path.Combine(root, "data");
        if (IsScarlettFoxRuntime(runtime))
        {
            // Apply the fork's measured run-118 steady-state set and run-128 memory
            // trims. Skip its diagnostic-only meters and the VRAM-heavy small-buffer
            // placement tweak: this launcher targets a 6 GB card.
            var scarlettProfile = new Dictionary<string, string>
            {
                ["VICTORY_REFRESH_ONCE"] = "1",
                ["VICTORY_BACK_OWN_VIEW"] = "1",
                ["VICTORY_WIDE_MERGE"] = "1",
                ["VICTORY_LEVELS_ON_DEMAND"] = "1",
                ["VICTORY_MIRROR_FAST"] = "1",
                ["VICTORY_PIPELINE_WAIT_MS"] = "20",
                ["VICTORY_ASYNC_SUBMIT"] = "1",
                ["VICTORY_QUEUE2"] = "1",
                ["VICTORY_INDIRECT_WINDOW"] = "1",
                ["VICTORY_JOIN_COPIES"] = "1",
                ["VICTORY_RELAY_COPY"] = "1",
                ["VICTORY_COPY_PLAIN"] = "1",
                ["VICTORY_WIDE_TEXTURES"] = "1",
                ["VICTORY_INDIRECT_GATHER"] = "1",
                ["VICTORY_LATE_WAIT_LISTS"] = "1024",
                ["VICTORY_CARD_FILLS_BATCH"] = "0",
                ["VICTORY_GATHER_DISPATCH"] = "0",
                ["VICTORY_RELAY"] = "2",
                ["VICTORY_WIDE_BACK"] = "1",
                ["VICTORY_CARD_STEPS"] = "2",
                ["VICTORY_INDIRECT_BATCH"] = "2",
                ["VICTORY_MAP_THREADS"] = "1",
                ["VICTORY_CARD_AWAKE"] = "1",
                ["VICTORY_JOIN_PARTS"] = "8",
                ["VICTORY_UPLOAD_AHEAD"] = "1",
                ["VICTORY_POOL_DISCARD"] = "1",
                ["VICTORY_PARTIAL_DELTA"] = "1",
                ["VICTORY_FILL_TITLE"] = "1",
                ["VICTORY_BAKE_DELTA"] = "1",
                ["VICTORY_MAP_LOCK_FINE"] = "1",
                ["VICTORY_STAGING_REST_MS"] = "250",
                ["VICTORY_STAGING_PACK"] = "1",
                ["VICTORY_SEND_SPARE_MB"] = "128",
                ["VICTORY_WINDOW_KB"] = "1024",
                ["VICTORY_WINDOW_TAILS"] = "1",
                ["VICTORY_HEAP_PACK"] = "1",
                ["VICTORY_LIST_POOL_IDLE_S"] = "10"
            };
            foreach (var (name, value) in scarlettProfile)
                start.Environment[name] = value;
            // The measured profile omits THIN; explicitly clear any shell override
            // that could trade image detail for a small descriptor-table saving.
            start.Environment["VICTORY_THIN"] = "0";
            start.Environment["VICTORY_DISPLAY_MODE"] = _gtaResolution;
            start.Environment["VICTORY_WINDOW_MAX"] = _gtaResolution;
            // The Scarlett-Fox fork leaves its costly console BVH builder off unless
            // this is explicitly set to 1. Force the choice so shell-level variables
            // cannot accidentally enable it for a 6 GB GPU.
            start.Environment["VICTORY_BVH_BUILDER"] = _scarlettRayTracing ? "1" : "0";
            start.Environment["VICTORY_AMBIENT"] = "0.8";
            // Scarlett-Fox records pipelines from actual game runs so warm --used can
            // compile those same pipeline keys before the next launch.
            start.Environment["VICTORY_PIPELINE_BOOK"] = "1";
        }
        if (nvidia)
        {
            // Match the fork's play-nvidia-8gb.ps1 profile for both prewarm and gameplay.
            foreach (var option in new[] { "SKIP_PIPELINES", "BAKE_TEXTURES_MS", "CARD_FETCH_INFLIGHT", "LEVELS_ON_DEMAND", "REFRESH_ONCE", "AMBIENT" })
                start.Environment.Remove("VICTORY_" + option);
            foreach (var option in new[] { "ASYNC_SUBMIT", "QUEUE2", "INDIRECT_WINDOW", "TEXTURE_NO_WAIT", "COPY_PLAIN", "SKIP_WC_COMPARE", "WIDE_TEXTURES" })
                start.Environment["VICTORY_" + option] = "1";
            start.Environment["VICTORY_TEXTURE_DROP_MIPS"] = "2";
            start.Environment.TryAdd("VICTORY_TEXTURE_FLOOR_MB", "768");
            start.Environment.TryAdd("VICTORY_CARD_BUFFERS_MB", "512");
        }
        else if (runtime == TrustNvidiaRuntime)
        {
            // Keep the fork's Series S interface plane and NVIDIA descriptor headroom.
            start.Environment.TryAdd("VICTORY_COMPOSE", "1");
            start.Environment.TryAdd("VICTORY_DYNAMIC_SLOTS", "524288");
        }
        return start;
    }

    // A skip returns to the launch flow; cancellation stops the launch itself.
    private async Task<VictoryPrewarmResult> PrewarmVictoryAsync(string executable, string gameExecutable, string runtime)
    {
        var nvidia = runtime == "victory-nvidia";
        var archive = new FileInfo(Path.Combine(Path.GetDirectoryName(gameExecutable)!, "shaders.rpf"));
        if (!archive.Exists)
        {
            SendDeveloperLog("Shader prewarm", "No shaders.rpf archive was found; the runtime will compile shaders during play.");
            return VictoryPrewarmResult.Ready;
        }
        var scarlettFox = IsScarlettFoxRuntime(runtime);
        var start = CreateVictoryStart(executable, gameExecutable, runtime, out var cache);
        var runtimeFile = new FileInfo(executable);
        var pipelineBook = Path.Combine(cache, "pipelines", "early-pipelines.txt");
        var pipelineFile = new FileInfo(pipelineBook);
        var warmDoneFile = new FileInfo(Path.Combine(cache, "pipelines", "warm-done.txt"));
        var pipelineStamp = scarlettFox
            ? $"rt={(_scarlettRayTracing ? 1 : 0)};book={(pipelineFile.Exists ? $"{pipelineFile.Length}:{pipelineFile.LastWriteTimeUtc.Ticks}" : "missing")};warm={(warmDoneFile.Exists ? $"{warmDoneFile.Length}:{warmDoneFile.LastWriteTimeUtc.Ticks}" : "missing")}"
            : "";
        var marker = Path.Combine(cache, scarlettFox ? "scarlett-fox-prewarm-v1" : "prewarm-shaders-v1");
        var stamp = $"{archive.Length}:{archive.LastWriteTimeUtc.Ticks}:{runtimeFile.Length}:{runtimeFile.LastWriteTimeUtc.Ticks}:{pipelineStamp}";
        try
        {
            if (File.Exists(marker) && File.ReadAllText(marker) == stamp &&
                Directory.Exists(Path.Combine(cache, "shaders")) &&
                Directory.EnumerateFiles(Path.Combine(cache, "shaders"), "*", SearchOption.AllDirectories).Any())
            {
                SendDeveloperLog("Shader prewarm", "The selected runtime's shader cache is current; reusing it.");
                return VictoryPrewarmResult.Ready;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        if (!ShaderPrebuildGate.WaitOne(0))
        {
            SendDeveloperLog("Shader prewarm", "Another shader job is active; launching with the selected runtime's existing cache.");
            ShowToast("Another shader job is active. This launch will compile shaders as needed.");
            return VictoryPrewarmResult.Ready;
        }

        using var cancellation = new CancellationTokenSource();
        _victoryPrewarmCancellation = cancellation;
        try
        {
            SetGtaLaunchSplash("shaders", "Reading the shader archive. The game opens when prewarming finishes.",
                runtimeLabel: nvidia ? NvidiaVictoryLabel : null);
            SendDeveloperLog("Shader prewarm", $"Starting {runtime} shader translation into its persistent cache.");
            start.Environment["VICTORY_LOG"] = "warn";
            if (scarlettFox)
            {
                var scarlettTranslated = await PrewarmScarlettFoxShadersAsync(start, archive.FullName, cancellation.Token);
                if (cancellation.IsCancellationRequested)
                    return _skipLaunchPrewarm ? VictoryPrewarmResult.Skipped : VictoryPrewarmResult.CancelLaunch;
                if (scarlettTranslated <= 0)
                {
                    ShowToast("Scarlett-Fox did not cache any shaders. The game will still launch and compile them as needed.");
                    return VictoryPrewarmResult.Ready;
                }

                pipelineFile.Refresh();
                if (pipelineFile.Exists && pipelineFile.Length > 0)
                {
                    SetGtaLaunchSplash("shaders", $"Prewarmed {scarlettTranslated:N0} shaders. Preparing pipelines recorded during earlier runs…");
                    await PrewarmScarlettFoxPipelinesAsync(executable, gameExecutable, runtime, cancellation.Token);
                }
                if (cancellation.IsCancellationRequested)
                    return _skipLaunchPrewarm ? VictoryPrewarmResult.Skipped : VictoryPrewarmResult.CancelLaunch;
                try { await File.WriteAllTextAsync(marker, stamp); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                return VictoryPrewarmResult.Ready;
            }
            if (nvidia) start.Environment.Remove("VICTORY_WARM_SECONDS");
            var arguments = runtime switch
            {
                "victory-nvidia" => new[] { "translate", archive.FullName, "--warm", "all", "--jobs", "2" },
                TrustNvidiaRuntime => new[] { "shaders", archive.FullName, "--jobs", "2" },
                _ => new[] { "warm", archive.FullName, "--jobs", "2", "--no-pipelines" }
            };
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);

            using var process = Process.Start(start) ?? throw new IOException("Could not start shader prewarming.");
            using var cancelProcess = cancellation.Token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            });

            var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            var last = -1;
            var translated = 0;
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (cancellation.IsCancellationRequested) continue;
                SendDeveloperLog("Shader prewarm", line);
                var parts = line.Split(' ');
                if (parts.Length == 3 && parts[0] == "warm-progress" &&
                    int.TryParse(parts[1], out var done) && int.TryParse(parts[2], out var total) &&
                    done > last && total > 0 && done <= total)
                {
                    last = done;
                    SendDashboardMessage(new { type = "victory-prewarm-progress", done, total });
                }
                else if (parts.Length == 2 && parts[0] == "warm-result")
                    int.TryParse(parts[1], out translated);
                else
                {
                    var progress = Regex.Match(line, @"warm-up\s+(?<done>[\d,]+)\s+shaders so far");
                    if (progress.Success && int.TryParse(progress.Groups["done"].Value.Replace(",", ""), out var processed))
                    {
                        translated = Math.Max(translated, processed);
                        SendDashboardMessage(new { type = "victory-prewarm-progress", done = processed, total = 0,
                            detail = $"{processed:N0} shader records processed by the runtime…" });
                    }
                    var summary = Regex.Match(line,
                        @"warm-up\s+.* hashes asked, (?<found>[\d,]+) found: (?<translated>[\d,]+) translated now, (?<cached>[\d,]+) in the cache already");
                    if (summary.Success &&
                        int.TryParse(summary.Groups["translated"].Value.Replace(",", ""), out var fresh) &&
                        int.TryParse(summary.Groups["cached"].Value.Replace(",", ""), out var cached))
                    {
                        translated = Math.Max(translated, fresh + cached);
                        var complete = fresh + cached;
                        SendDashboardMessage(new { type = "victory-prewarm-progress", done = complete, total = complete,
                            detail = $"Shader cache ready: {fresh:N0} translated, {cached:N0} already cached." });
                    }
                }
            }
            await Task.WhenAll(process.WaitForExitAsync(), stderr);
            if (cancellation.IsCancellationRequested) return _skipLaunchPrewarm ? VictoryPrewarmResult.Skipped : VictoryPrewarmResult.CancelLaunch;
            if (process.ExitCode != 0 || translated <= 0)
            {
                ShowToast("No shader cache was prepared. The game will compile shaders as needed.");
                return VictoryPrewarmResult.Ready;
            }
            try { await File.WriteAllTextAsync(marker, stamp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return cancellation.IsCancellationRequested
                ? _skipLaunchPrewarm ? VictoryPrewarmResult.Skipped : VictoryPrewarmResult.CancelLaunch
                : VictoryPrewarmResult.Ready;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            if (cancellation.IsCancellationRequested) return _skipLaunchPrewarm ? VictoryPrewarmResult.Skipped : VictoryPrewarmResult.CancelLaunch;
            ShowToast("Shader prewarming could not finish. The game will compile shaders as needed.");
            return VictoryPrewarmResult.Ready;
        }
        finally
        {
            _victoryPrewarmCancellation = null;
            _skipLaunchPrewarm = false;
            ShaderPrebuildGate.Release();
        }
    }

    private async Task<int> PrewarmScarlettFoxShadersAsync(ProcessStartInfo start, string archive, CancellationToken cancellation)
    {
        start.ArgumentList.Clear();
        foreach (var argument in new[] { "shaders", archive, "--jobs", "2" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start Scarlett-Fox shader translation.");
        using var stop = cancellation.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        });
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        var translated = 0;
        while (await process.StandardOutput.ReadLineAsync() is { } line)
        {
            SendDeveloperLog("Shader prewarm", line);
            var match = Regex.Match(line, @"^(?<count>\d+) shaders in the cache");
            if (!match.Success || !int.TryParse(match.Groups["count"].Value, out var count)) continue;
            translated = count;
            var final = line.Contains("do not translate", StringComparison.Ordinal);
            SendDashboardMessage(new
            {
                type = "victory-prewarm-progress",
                done = count,
                total = final ? count : 0,
                detail = $"{count:N0} shaders translated into Scarlett-Fox's persistent cache."
            });
        }
        await Task.WhenAll(process.WaitForExitAsync(), stderr);
        cancellation.ThrowIfCancellationRequested();
        return process.ExitCode == 0 ? translated : 0;
    }

    private async Task PrewarmScarlettFoxPipelinesAsync(string executable, string gameExecutable, string runtime, CancellationToken cancellation)
    {
        var start = CreateVictoryStart(executable, gameExecutable, runtime, out _);
        start.Environment["VICTORY_LOG"] = "warn";
        foreach (var argument in new[] { "warm", "--used", "--jobs", "1" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start Scarlett-Fox pipeline prewarming.");
        using var stop = cancellation.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        });
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        var total = 0;
        var fingerprint = "";
        var runtimeCache = start.Environment["VICTORY_CACHE_DIR"]
            ?? throw new InvalidOperationException("Scarlett-Fox cache path was not configured.");
        var warmDonePath = Path.Combine(runtimeCache, "pipelines", "warm-done.txt");
        var baseline = new HashSet<string>(StringComparer.Ordinal);
        var lastPublished = -1;

        void PublishCompletedPipelineProgress()
        {
            if (total <= 0 || fingerprint.Length == 0) return;
            try
            {
                var completed = File.ReadLines(warmDonePath)
                    .Where(line => line.StartsWith(fingerprint + " ", StringComparison.Ordinal))
                    .Select(line => line[(fingerprint.Length + 1)..])
                    .Distinct(StringComparer.Ordinal)
                    .Count(key => !baseline.Contains(key));
                completed = Math.Min(total, completed);
                if (completed == lastPublished) return;
                lastPublished = completed;
                SendDashboardMessage(new { type = "victory-prewarm-progress", done = completed, total, detail = $"Warming previously used render pipelines · {completed:N0} of {total:N0}" });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        var readLine = process.StandardOutput.ReadLineAsync();
        while (true)
        {
            var readOrTick = await Task.WhenAny(readLine, Task.Delay(750, cancellation));
            if (readOrTick != readLine)
            {
                cancellation.ThrowIfCancellationRequested();
                PublishCompletedPipelineProgress();
                continue;
            }
            var line = await readLine;
            if (line is null) break;
            SendDeveloperLog("Pipeline prewarm", line);
            var summary = Regex.Match(line, @"(?<total>\d+) to make,\s*\d+ processes");
            if (summary.Success)
            {
                int.TryParse(summary.Groups["total"].Value, out total);
                var fingerprintMatch = Regex.Match(line, @"\((?<fingerprint>[0-9a-f]+)\)", RegexOptions.IgnoreCase);
                if (fingerprintMatch.Success) fingerprint = fingerprintMatch.Groups["fingerprint"].Value;
                if (fingerprint.Length > 0)
                {
                    try
                    {
                        foreach (var entry in File.ReadLines(warmDonePath))
                        {
                            if (entry.StartsWith(fingerprint + " ", StringComparison.Ordinal))
                                baseline.Add(entry[(fingerprint.Length + 1)..]);
                        }
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                lastPublished = -1;
                PublishCompletedPipelineProgress();
            }
            var progress = Regex.Match(line, @"^(?<done>\d+) of (?<total>\d+),");
            if (progress.Success && int.TryParse(progress.Groups["done"].Value, out var done) &&
                int.TryParse(progress.Groups["total"].Value, out var progressTotal))
            {
                total = progressTotal;
                fingerprint = fingerprint.Length > 0 ? fingerprint : "";
                SendDashboardMessage(new { type = "victory-prewarm-progress", done, total, detail = "Warming previously used render pipelines…" });
            }
            else if (summary.Success)
                SendDashboardMessage(new { type = "victory-prewarm-progress", done = Math.Max(0, lastPublished), total, detail = total > 0 ? $"Preparing {total:N0} previously used render pipelines…" : "No previously used render pipelines need warming yet." });
            PublishCompletedPipelineProgress();
            readLine = process.StandardOutput.ReadLineAsync();
        }
        await Task.WhenAll(process.WaitForExitAsync(), stderr);
        cancellation.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) ShowToast($"Some Scarlett-Fox pipelines could not be prewarmed (exit {process.ExitCode}); the game will compile remaining ones while running.");
    }
}
