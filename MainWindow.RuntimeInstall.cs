using System.Diagnostics;
using System.IO;

namespace XboxEmulatorUI;

public partial class MainWindow
{
    private sealed record VictoryBuild(string Name, string Directory, string Repository, string Revision, string Patch, string? PatchInclude = null);

    private static readonly IReadOnlyDictionary<string, VictoryBuild> VictoryBuilds = new Dictionary<string, VictoryBuild>
    {
        ["victory"] = new("VIctory World", "victory-world-runtime", "https://github.com/ZSlayerHQ/zslayer-pc-port-runtime.git",
            "99f8834aa16b98d7f3943b8de262a8d4857bda7b", "VictoryWorld.Nebula.patch"),
        ["victory-nvidia"] = new("Nvidia 8g", "victory-nvidia-8gb", "https://github.com/soldsawn/victory-nvidia-8gb.git",
            "a14d8c88c12dfb00e0c0361b99d3d44addc620de", "VictoryNvidia.Nebula.patch"),
        ["victory-amd"] = new("VIctory AMD", "victory-amd", "https://github.com/myokex/victory-amd.git",
            "070df424b0967dca422478a1f12afaaf6736ebdd", "VictoryWorld.Nebula.patch", "--include=apps/victory/src/translate.rs")
    };
    private bool _victoryWorldInstalling;
    private string? _victoryWorldInstallStatus;

    private static string? FindGitExecutable() => FindRuntimeTool("git.exe",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git", "cmd"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git", "cmd"));

    private static string? FindCargoExecutable()
    {
        var cargoHome = Environment.GetEnvironmentVariable("CARGO_HOME", EnvironmentVariableTarget.User)
            ?? Environment.GetEnvironmentVariable("CARGO_HOME");
        return FindRuntimeTool("cargo.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cargo", "bin"),
            string.IsNullOrWhiteSpace(cargoHome) ? null : Path.Combine(cargoHome, "bin"));
    }

    private static string? FindRuntimeTool(string file, params string?[] knownDirectories)
    {
        // A running launcher still has its old PATH after WinGet installs a tool.
        var paths = new[] { Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) };
        foreach (var directory in knownDirectories.Concat(paths.SelectMany(path => (path ?? "").Split(Path.PathSeparator))))
        {
            var expanded = Environment.ExpandEnvironmentVariables(directory?.Trim().Trim('"') ?? "");
            if (!Path.IsPathFullyQualified(expanded)) continue;
            var executable = Path.Combine(expanded, file);
            if (File.Exists(executable)) return executable;
        }
        return null;
    }

    private async Task InstallRuntimePrerequisiteAsync(string tool)
    {
        if (_victoryWorldInstalling) return;
        var prerequisite = tool switch
        {
            "git" => (Name: "Git", Id: "Git.Git", Find: (Func<string?>)FindGitExecutable, Help: "https://git-scm.com/install/windows"),
            "rust" => (Name: "Rust", Id: "Rustlang.Rustup", Find: (Func<string?>)FindCargoExecutable, Help: "https://rustup.rs/"),
            _ => throw new ArgumentException("Unknown VIctory prerequisite.")
        };
        if (prerequisite.Find() is not null)
        {
            ShowToast($"{prerequisite.Name} is already installed.");
            PublishDashboardState();
            return;
        }

        var winget = FindRuntimeTool("winget.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps"));
        if (winget is null)
        {
            OpenWebPage(prerequisite.Help);
            ShowToast($"WinGet is unavailable. The official {prerequisite.Name} installer page opened in your browser.");
            return;
        }

        _victoryWorldInstalling = true;
        _victoryWorldInstallStatus = $"Installing {prerequisite.Name}. Follow the WinGet and installer prompts…";
        PublishDashboardState();
        try
        {
            var start = new ProcessStartInfo(winget) { UseShellExecute = true };
            foreach (var argument in new[] { "install", "--id", prerequisite.Id, "--exact", "--source", "winget", "--interactive" })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException("Could not open WinGet.");
            await process.WaitForExitAsync();
            _victoryWorldInstallStatus = prerequisite.Find() is not null
                ? $"{prerequisite.Name} is installed."
                : process.ExitCode == 0
                    ? $"Restart Nebula to finish detecting {prerequisite.Name}."
                    : $"{prerequisite.Name} installation ended with code 0x{process.ExitCode:X8}.";
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception)
        {
            _victoryWorldInstallStatus = $"Could not run the {prerequisite.Name} installer. {error.Message}";
        }
        finally
        {
            _victoryWorldInstalling = false;
            if (IsLoaded) PublishDashboardState();
        }
    }

    private static bool IsVictoryWorldRepository(string? input)
    {
        if (!Uri.TryCreate(input?.Trim(), UriKind.Absolute, out var url) ||
            url.Scheme != Uri.UriSchemeHttps || url.Host != "github.com" || !url.IsDefaultPort ||
            url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0)
            return false;

        var path = url.AbsolutePath.TrimEnd('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        return path.Equals("/ZSlayerHQ/victory-world-runtime", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/ZSlayerHQ/zslayer-pc-port-runtime", StringComparison.OrdinalIgnoreCase);
    }

    private Task InstallVictoryWorldAsync(string? repository)
    {
        if (!IsVictoryWorldRepository(repository))
            throw new ArgumentException("Enter the GitHub HTTPS URL for ZSlayerHQ’s VIctory World repository.");
        return InstallVictoryBuildAsync("victory");
    }

    private async Task InstallVictoryBuildAsync(string id)
    {
        if (_victoryWorldInstalling || !VictoryBuilds.TryGetValue(id, out var build)) return;
        if (FindVictoryRuntime(id) is not null) return;
        var git = FindGitExecutable();
        var cargo = FindCargoExecutable();
        if (git is null || cargo is null) throw new IOException("Install Git and Rust before building VIctory.");

        var root = Directory.CreateDirectory(LocalRuntimeFolder).FullName;
        var target = Path.Combine(root, build.Directory);
        var staging = Directory.CreateDirectory(Path.Combine(root, $".victory-install-{Guid.NewGuid():N}")).FullName;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(25));
        void CancelOnClose(object? sender, EventArgs args) => cancellation.Cancel();
        Closed += CancelOnClose;
        _victoryWorldInstalling = true;
        try
        {
            _victoryWorldInstallStatus = $"Downloading {build.Name}…";
            PublishDashboardState();
            await RunRuntimeBuildStepAsync(git, staging, cancellation.Token, "init", "--quiet");
            await RunRuntimeBuildStepAsync(git, staging, cancellation.Token,
                "fetch", "--depth=1", "--no-tags", build.Repository, build.Revision);
            await RunRuntimeBuildStepAsync(git, staging, cancellation.Token, "checkout", "--detach", "FETCH_HEAD");

            var patch = Path.Combine(staging, "nebula.patch");
            await using (var source = typeof(MainWindow).Assembly.GetManifestResourceStream(build.Patch)
                ?? throw new IOException("The launcher’s shader progress patch is missing."))
            await using (var output = File.Create(patch))
                await source.CopyToAsync(output, cancellation.Token);
            var apply = build.PatchInclude is null ? new[] { "apply", "--", patch } : ["apply", build.PatchInclude, "--", patch];
            await RunRuntimeBuildStepAsync(git, staging, cancellation.Token, apply);

            _victoryWorldInstallStatus = $"Building {build.Name} locally. This can take several minutes…";
            PublishDashboardState();
            await RunRuntimeBuildStepAsync(cargo, staging, cancellation.Token,
                "build", "--release", "--locked", "-p", "victory");

            Directory.CreateDirectory(target);
            File.Copy(Path.Combine(staging, "LICENSE"), Path.Combine(target, "LICENSE"), overwrite: true);
            File.Move(Path.Combine(staging, "target", "release", "victory.exe"),
                Path.Combine(target, "victory.exe"), overwrite: true);
            if (FindGtaRuntime(_gtaRuntime) is null)
            {
                _gtaRuntime = id;
                SaveSettings();
            }
            _victoryWorldInstallStatus = $"{build.Name} is installed.";
        }
        catch (Exception error)
        {
            _victoryWorldInstallStatus = error is OperationCanceledException
                ? $"{build.Name} installation stopped."
                : error.Message;
        }
        finally
        {
            Closed -= CancelOnClose;
            _victoryWorldInstalling = false;
            if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(staging)), root, StringComparison.OrdinalIgnoreCase))
            {
                try { Directory.Delete(staging, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            if (IsLoaded) PublishDashboardState();
        }
    }

    private static async Task RunRuntimeBuildStepAsync(string executable, string folder, CancellationToken cancellation, params string[] args)
    {
        cancellation.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = folder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        // Use this checkout's build directory even if the parent's Cargo configuration overrides it.
        start.Environment["CARGO_TARGET_DIR"] = Path.Combine(folder, "target");
        foreach (var argument in args) start.ArgumentList.Add(argument);

        Process process;
        try { process = Process.Start(start) ?? throw new IOException("Could not start the local runtime installer."); }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new IOException($"Could not start {Path.GetFileName(executable)}. Install Git and Rust, then reopen Nebula.");
        }
        using (process)
        {
            using var stop = cancellation.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            });
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(cancellation);
            await Task.WhenAll(stdout, stderr);
            cancellation.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
            {
                var detail = (await stderr).Replace(folder, "[build folder]", StringComparison.OrdinalIgnoreCase).Trim();
                throw new IOException($"{Path.GetFileName(executable)} {args[0]} failed. {LimitReport(detail)}");
            }
        }
    }
}
