using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XboxEmulatorUI;

public partial class MainWindow
{
    private const string CustomRuntimeManifestName = "nebula-custom-runtime.json";
    private static readonly JsonSerializerOptions CustomRuntimeJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private sealed record CustomVictoryRuntime(string Id, string Name, string Repository, string Branch, string Revision, string Directory);

    private static bool TryNormalizeCustomRepository(string? input, string? branchInput,
        out string repository, out string branch, out string name, out string error)
    {
        repository = branch = name = error = "";
        if (!Uri.TryCreate(input?.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com" || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath.Contains('%'))
        {
            error = "Enter a public GitHub repository URL such as https://github.com/owner/repository.";
            return false;
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length != 2 || segments.Any(segment => !Regex.IsMatch(segment, "^[A-Za-z0-9_.-]+$")) ||
            segments.Any(segment => segment is "." or ".."))
        {
            error = "Use the repository's main GitHub URL, without /tree, /releases, or a file path.";
            return false;
        }

        var owner = segments[0];
        var repo = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? segments[1][..^4] : segments[1];
        if (string.IsNullOrWhiteSpace(repo))
        {
            error = "The repository name is missing.";
            return false;
        }

        branch = branchInput?.Trim() ?? "";
        if (branch.Length > 96 || (branch.Length > 0 &&
            (!Regex.IsMatch(branch, "^[A-Za-z0-9][A-Za-z0-9._/-]*$") || branch.Contains("..", StringComparison.Ordinal) ||
             branch.Contains("//", StringComparison.Ordinal) || branch.EndsWith('/') || branch.EndsWith('.') || branch.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))))
        {
            error = "Enter a valid branch name, or leave it blank to use the repository's default branch.";
            return false;
        }

        repository = $"https://github.com/{owner}/{repo}.git";
        name = repo + (branch.Length == 0 ? "" : $" · {branch}");
        return true;
    }

    private static string CustomRuntimeId(string repository, string branch)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{repository}\n{branch}")))
            .ToLowerInvariant()[..12];
        return $"custom-{hash}";
    }

    private static IReadOnlyList<CustomVictoryRuntime> GetCustomVictoryRuntimes()
    {
        if (!Directory.Exists(LocalRuntimeFolder)) return [];
        var runtimes = new List<CustomVictoryRuntime>();
        foreach (var directory in Directory.EnumerateDirectories(LocalRuntimeFolder))
        {
            try
            {
                var manifestPath = Path.Combine(directory, CustomRuntimeManifestName);
                if (!File.Exists(manifestPath) || !File.Exists(Path.Combine(directory, "victory.exe"))) continue;
                var manifest = JsonSerializer.Deserialize<CustomVictoryRuntime>(File.ReadAllText(manifestPath), CustomRuntimeJsonOptions);
                if (manifest is null || !Regex.IsMatch(manifest.Id, "^custom-[a-f0-9]{12}$") ||
                    !string.Equals(Path.GetFileName(directory), manifest.Id, StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(manifest.Name) || string.IsNullOrWhiteSpace(manifest.Repository)) continue;
                runtimes.Add(manifest with { Directory = directory });
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                // Ignore incomplete or invalid custom runtime installations.
            }
        }
        return runtimes.OrderBy(runtime => runtime.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static CustomVictoryRuntime? GetCustomVictoryRuntime(string id)
        => GetCustomVictoryRuntimes().FirstOrDefault(runtime => runtime.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    private static bool IsScarlettFoxRuntime(string id)
    {
        var repository = GetCustomVictoryRuntime(id)?.Repository.TrimEnd('/');
        if (repository is null) return false;
        if (repository.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repository = repository[..^4];
        return repository.Equals("https://github.com/Op-lite/victory-scarlett", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindCustomVictoryRuntime(string id)
    {
        var runtime = GetCustomVictoryRuntime(id);
        var executable = runtime is null ? null : Path.Combine(runtime.Directory, "victory.exe");
        return executable is not null && File.Exists(executable) ? executable : null;
    }

    private async Task InstallCustomVictoryAsync(string? inputRepository, string? inputBranch)
    {
        if (_victoryWorldInstalling) return;
        if (!TryNormalizeCustomRepository(inputRepository, inputBranch, out var repository, out var branch, out var name, out var error))
            throw new ArgumentException(error);

        var git = FindGitExecutable();
        var cargo = FindCargoExecutable();
        if (git is null || cargo is null) throw new IOException("Install Git and Rust before building a custom VIctory runtime.");

        var id = CustomRuntimeId(repository, branch);
        var target = Path.Combine(LocalRuntimeFolder, id);
        var staging = Directory.CreateDirectory(Path.Combine(LocalRuntimeFolder, $".custom-runtime-{Guid.NewGuid():N}")).FullName;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(35));
        void CancelOnClose(object? sender, EventArgs args) => cancellation.Cancel();
        Closed += CancelOnClose;
        _victoryWorldInstalling = true;
        _victoryInstallBuildId = id;
        _victoryInstallLatest = true;
        try
        {
            _victoryWorldInstallStatus = $"Fetching {name} from GitHub…";
            PublishDashboardState();
            await RunRuntimeBuildStepAsync(git, staging, cancellation.Token, "init", "--quiet");
            if (branch.Length > 0)
            {
                var remoteRefs = await RunRuntimeBuildStepAsync(git, staging, cancellation.Token, "ls-remote", "--heads", repository);
                var branches = remoteRefs.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Split('\t', 2))
                    .Where(parts => parts.Length == 2 && parts[1].StartsWith("refs/heads/", StringComparison.Ordinal))
                    .Select(parts => parts[1]["refs/heads/".Length..])
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var exact = branches.FirstOrDefault(candidate => candidate.Equals(branch, StringComparison.Ordinal));
                if (exact is not null) branch = exact;
                else
                {
                    var caseMatches = branches.Where(candidate => candidate.Equals(branch, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (caseMatches.Length == 1) branch = caseMatches[0];
                    else if (caseMatches.Length > 1)
                        throw new IOException($"Branch '{branch}' differs only by letter case. Enter the exact branch name: {string.Join(", ", caseMatches)}.");
                    else
                    {
                        var available = branches.Length == 0 ? "No branches were found." : $"Available branches: {string.Join(", ", branches.Take(12))}{(branches.Length > 12 ? ", …" : "")}.";
                        throw new IOException($"GitHub repository has no branch named '{branch}'. Check the repository URL and branch field. {available}");
                    }
                }
                var repositoryName = repository[(repository.LastIndexOf('/') + 1)..];
                if (repositoryName.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repositoryName = repositoryName[..^4];
                name = $"{repositoryName} · {branch}";
                id = CustomRuntimeId(repository, branch);
                target = Path.Combine(LocalRuntimeFolder, id);
                _victoryInstallBuildId = id;
                _victoryWorldInstallStatus = $"Found branch {branch}. Fetching {name}…";
                PublishDashboardState();
            }
            var fetch = branch.Length == 0
                ? new[] { "fetch", "--depth=1", "--no-tags", repository }
                : new[] { "fetch", "--depth=1", "--no-tags", repository, branch };
            await RunRuntimeBuildStepAsync(git, staging, cancellation.Token, fetch);
            await RunRuntimeBuildStepAsync(git, staging, cancellation.Token, "checkout", "--detach", "FETCH_HEAD");
            var revision = (await RunRuntimeBuildStepAsync(git, staging, cancellation.Token, "rev-parse", "HEAD")).Trim();
            if (!File.Exists(Path.Combine(staging, "Cargo.toml")))
                throw new IOException("This repository has no root Cargo.toml. Custom runtimes must be Rust VIctory forks with a root Cargo workspace.");

            if (!await ScanRuntimeBeforeBuildAsync(staging, name, cancellation.Token))
                throw new OperationCanceledException("Custom runtime build cancelled during security review.");

            _victoryWorldInstallStatus = $"Building {name}. Custom repository build scripts will run locally…";
            PublishDashboardState();
            var cargoArguments = File.Exists(Path.Combine(staging, "Cargo.lock"))
                ? new[] { "build", "--release", "--locked", "-p", "victory" }
                : new[] { "build", "--release", "-p", "victory" };
            await RunRuntimeBuildStepAsync(cargo, staging, cancellation.Token, cargoArguments);
            var releaseDirectory = Path.Combine(staging, "target", "release");
            var builtExecutable = Path.Combine(releaseDirectory, "victory.exe");
            if (!File.Exists(builtExecutable))
                throw new IOException("The build completed without target/release/victory.exe. The repository must build the Victory CLI package named 'victory'.");

            Directory.CreateDirectory(target);
            foreach (var artifact in Directory.EnumerateFiles(releaseDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                var fileName = Path.GetFileName(artifact);
                if (fileName.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".d", StringComparison.OrdinalIgnoreCase)) continue;
                File.Copy(artifact, Path.Combine(target, fileName), overwrite: true);
            }
            var license = Path.Combine(staging, "LICENSE");
            if (File.Exists(license)) File.Copy(license, Path.Combine(target, "LICENSE"), overwrite: true);
            var manifest = new CustomVictoryRuntime(id, name, repository, branch, revision, id);
            File.WriteAllText(Path.Combine(target, CustomRuntimeManifestName),
                JsonSerializer.Serialize(manifest, CustomRuntimeJsonOptions));

            if (FindGtaRuntime(_gtaRuntime) is null)
            {
                _gtaRuntime = id;
                SaveSettings();
            }
            _victoryWorldInstallStatus = $"Custom runtime {name} is installed ({revision[..Math.Min(7, revision.Length)]}). Choose it from Compatibility runtime.";
        }
        catch (Exception installError)
        {
            _victoryWorldInstallStatus = installError is OperationCanceledException
                ? $"Custom runtime {name} build cancelled."
                : installError.Message;
        }
        finally
        {
            Closed -= CancelOnClose;
            _victoryWorldInstalling = false;
            _victoryInstallBuildId = null;
            _victoryInstallLatest = false;
            if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(staging)), Path.GetFullPath(LocalRuntimeFolder), StringComparison.OrdinalIgnoreCase))
            {
                try { Directory.Delete(staging, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            if (IsLoaded) PublishDashboardState();
        }
    }
}
