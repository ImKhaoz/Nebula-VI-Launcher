using System.Diagnostics;
using System.IO;

namespace XboxEmulatorUI;

public partial class MainWindow
{
    private CancellationTokenSource? _victoryPrewarmCancellation;

    private static ProcessStartInfo CreateVictoryStart(string executable, string gameExecutable, string runtime, out string cache)
    {
        var nvidia = runtime == "victory-nvidia";
        var root = Path.Combine(LauncherDataRoot, "VIctory");
        cache = Path.Combine(root, runtime switch
        {
            "victory-nvidia" => "nvidia-8gb-cache",
            TrustNvidiaRuntime => "trustno1-nvidia-cache",
            "victory-amd" => "amd-cache",
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

    // False means the user cancelled the game launch.
    private async Task<bool> PrewarmVictoryAsync(string executable, string gameExecutable, string runtime)
    {
        var nvidia = runtime == "victory-nvidia";
        var archive = new FileInfo(Path.Combine(Path.GetDirectoryName(gameExecutable)!, "shaders.rpf"));
        if (!archive.Exists) return true;
        var start = CreateVictoryStart(executable, gameExecutable, runtime, out var cache);
        var runtimeFile = new FileInfo(executable);
        var marker = Path.Combine(cache, "prewarm-shaders-v1");
        var stamp = $"{archive.Length}:{archive.LastWriteTimeUtc.Ticks}:{runtimeFile.Length}:{runtimeFile.LastWriteTimeUtc.Ticks}";
        try
        {
            if (File.Exists(marker) && File.ReadAllText(marker) == stamp &&
                Directory.Exists(Path.Combine(cache, "shaders")) &&
                Directory.EnumerateFiles(Path.Combine(cache, "shaders"), "*", SearchOption.AllDirectories).Any())
                return true;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        if (!ShaderPrebuildGate.WaitOne(0))
        {
            ShowToast("Another shader job is active. This launch will compile shaders as needed.");
            return true;
        }

        using var cancellation = new CancellationTokenSource();
        _victoryPrewarmCancellation = cancellation;
        try
        {
            SetGtaLaunchSplash("shaders", "Reading the shader archive. The game opens when prewarming finishes.",
                runtimeLabel: nvidia ? NvidiaVictoryLabel : null);
            start.Environment["VICTORY_LOG"] = "warn";
            if (nvidia) start.Environment.Remove("VICTORY_WARM_SECONDS");
            var arguments = runtime switch
            {
                "victory-nvidia" => new[] { "translate", archive.FullName, "--warm", "all", "--probe-host", "--jobs", "2" },
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
            }
            await Task.WhenAll(process.WaitForExitAsync(), stderr);
            if (cancellation.IsCancellationRequested) return false;
            if (process.ExitCode != 0 || translated <= 0)
            {
                ShowToast("No shader cache was prepared. The game will compile shaders as needed.");
                return true;
            }
            try { await File.WriteAllTextAsync(marker, stamp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return !cancellation.IsCancellationRequested;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            if (cancellation.IsCancellationRequested) return false;
            ShowToast("Shader prewarming could not finish. The game will compile shaders as needed.");
            return true;
        }
        finally
        {
            _victoryPrewarmCancellation = null;
            ShaderPrebuildGate.Release();
        }
    }
}
