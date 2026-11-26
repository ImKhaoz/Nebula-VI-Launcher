using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace XboxEmulatorUI;

public partial class MainWindow
{
    private static readonly HttpClient ReleaseHttp = CreateReleaseHttpClient();
    private static string LauncherVersion => typeof(MainWindow).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.1.0-preview.19";
    private static string NebulaReleaseRepository => typeof(MainWindow).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(item => item.Key == "NebulaReleaseRepository")?.Value ?? "";
    private static string LatestLauncherReleaseUrl => $"https://github.com/{NebulaReleaseRepository}/releases/latest";

    private static HttpClient CreateReleaseHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(7) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Nebula-GTAVI-Launcher");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private async Task CheckLauncherUpdateAsync()
    {
        if (!GtaOnlyEdition || string.IsNullOrWhiteSpace(NebulaReleaseRepository)) return;
        try
        {
            using var response = await ReleaseHttp.GetAsync(
                $"https://api.github.com/repos/{NebulaReleaseRepository}/releases/latest");
            if (!response.IsSuccessStatusCode) return;
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var data = await JsonDocument.ParseAsync(stream);
            var root = data.RootElement;
            var tag = root.TryGetProperty("tag_name", out var tagValue) ? tagValue.GetString() : null;
            var url = root.TryGetProperty("html_url", out var urlValue) ? urlValue.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag) || !IsNewerVersion(tag, LauncherVersion) ||
                !Uri.TryCreate(url, UriKind.Absolute, out var releaseUri) || releaseUri.Host != "github.com" || releaseUri.Scheme != "https")
                return;
            await Dispatcher.InvokeAsync(() => SendDashboardMessage(new
            {
                type = "launcher-update",
                currentVersion = LauncherVersion,
                latestVersion = tag,
                url = releaseUri.AbsoluteUri
            }));
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            LogDashboard($"Release check skipped: {error.GetType().Name}.");
        }
    }

    private static bool IsNewerVersion(string candidate, string current)
    {
        static bool TryParse(string value, out Version version, out string? prerelease)
        {
            value = value.Trim().TrimStart('v', 'V');
            var metadata = value.IndexOf('+');
            if (metadata >= 0) value = value[..metadata];
            var dash = value.IndexOf('-');
            prerelease = dash >= 0 ? value[(dash + 1)..] : null;
            if (dash >= 0) value = value[..dash];
            var parts = value.Split('.');
            if (parts.Length is < 2 or > 4 || parts.Any(part => !int.TryParse(part, out _)))
            {
                version = new Version(0, 0, 0);
                return false;
            }
            var padded = parts.Select(int.Parse).Concat(Enumerable.Repeat(0, 4)).Take(4).ToArray();
            version = new Version(padded[0], padded[1], padded[2], padded[3]);
            return true;
        }

        if (!TryParse(candidate, out var next, out var nextPrerelease) ||
            !TryParse(current, out var installed, out var installedPrerelease)) return false;
        var comparison = next.CompareTo(installed);
        if (comparison != 0) return comparison > 0;
        if (nextPrerelease is null || installedPrerelease is null)
            return nextPrerelease is null && installedPrerelease is not null;

        var nextParts = nextPrerelease.Split('.');
        var installedParts = installedPrerelease.Split('.');
        for (var index = 0; index < Math.Max(nextParts.Length, installedParts.Length); index++)
        {
            if (index >= nextParts.Length) return false;
            if (index >= installedParts.Length) return true;
            var leftNumeric = int.TryParse(nextParts[index], out var left);
            var rightNumeric = int.TryParse(installedParts[index], out var right);
            comparison = leftNumeric && rightNumeric ? left.CompareTo(right)
                : leftNumeric != rightNumeric ? (leftNumeric ? -1 : 1)
                : string.Compare(nextParts[index], installedParts[index], StringComparison.Ordinal);
            if (comparison != 0) return comparison > 0;
        }
        return false;
    }
}
