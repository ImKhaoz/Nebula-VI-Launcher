using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace XboxEmulatorUI;

public partial class MainWindow
{
    private TaskCompletionSource<bool>? _runtimeSecurityDecision;

    private sealed record RuntimeSecurityFinding(string ThreatName, string ThreatId, string[] Files, bool ActionSucceeded);

    private static string? FindDefenderCommand()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var direct = Path.Combine(programFiles, "Windows Defender", "MpCmdRun.exe");
        if (File.Exists(direct)) return direct;
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows Defender", "Platform"),
            Path.Combine(programFiles, "Windows Defender", "Platform")
        };
        foreach (var platform in roots.Where(Directory.Exists))
        {
            var executable = Directory.EnumerateDirectories(platform)
                .OrderByDescending(path => Version.TryParse(Path.GetFileName(path).Split('-')[0], out var version) ? version : new Version())
                .Select(path => Path.Combine(path, "MpCmdRun.exe"))
                .FirstOrDefault(File.Exists);
            if (executable is not null) return executable;
        }
        return null;
    }

    private async Task<bool> ScanRuntimeBeforeBuildAsync(string sourceFolder, string runtimeName, CancellationToken cancellation)
    {
        var defender = FindDefenderCommand();
        if (defender is null)
            return await ReviewRuntimeSecurityAsync(runtimeName, false,
                "Microsoft Defender's command-line scanner was not found. Review the source repository before continuing.", [], cancellation);

        _victoryWorldInstallStatus = "Scanning " + runtimeName + " source with Microsoft Defender…";
        PublishDashboardState();
        var start = new ProcessStartInfo(defender)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "-Scan", "-ScanType", "3", "-File", sourceFolder, "-DisableRemediation" })
            start.ArgumentList.Add(argument);

        int scanExitCode;
        string scanOutput;
        try
        {
            using var process = Process.Start(start) ?? throw new IOException("Microsoft Defender could not be started.");
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
            scanExitCode = process.ExitCode;
            scanOutput = (await stdout) + Environment.NewLine + (await stderr);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return await ReviewRuntimeSecurityAsync(runtimeName, false,
                "Microsoft Defender could not scan this source: " + error.Message, [], cancellation);
        }

        IReadOnlyList<RuntimeSecurityFinding> findings;
        try
        {
            findings = await ReadRuntimeThreatFindingsAsync(sourceFolder, cancellation);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or JsonException)
        {
            var detail = string.IsNullOrWhiteSpace(scanOutput) ? error.Message : LimitReport(scanOutput);
            return await ReviewRuntimeSecurityAsync(runtimeName, false,
                "The Defender scan finished, but its results could not be verified (exit " + scanExitCode + "). " + detail, [], cancellation);
        }

        if (findings.Count > 0)
            return await ReviewRuntimeSecurityAsync(runtimeName, true,
                "Microsoft Defender reported " + findings.Count + " finding(s) in the downloaded runtime source. Remediation was disabled for this scan; inspect the names and files below.", findings, cancellation);

        if (scanExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(scanOutput) ? "Defender exited with code " + scanExitCode + "." : LimitReport(scanOutput);
            return await ReviewRuntimeSecurityAsync(runtimeName, false,
                "Microsoft Defender did not confirm a clean scan (exit " + scanExitCode + "). " + detail, [], cancellation);
        }

        _victoryWorldInstallStatus = "Microsoft Defender found no threats in " + runtimeName + " source. Building…";
        PublishDashboardState();
        return true;
    }

    private async Task<IReadOnlyList<RuntimeSecurityFinding>> ReadRuntimeThreatFindingsAsync(string sourceFolder, CancellationToken cancellation)
    {
        var root = Path.GetFullPath(sourceFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var escapedRoot = root.Replace("'", "''", StringComparison.Ordinal);
        var query = "$ErrorActionPreference='Stop'; try { $root='" + escapedRoot + "'; $hits=@(Get-MpThreatDetection | Where-Object { $_.Resources -and (($_.Resources | Where-Object { $_.Contains($root) }).Count -gt 0) } | ForEach-Object { $d=$_; $t=Get-MpThreat -ThreatID $d.ThreatID -ErrorAction SilentlyContinue; [pscustomobject]@{ threatId=[string]$d.ThreatID; threatName=if($t.ThreatName){$t.ThreatName}else{'Detected item'}; resources=@($d.Resources | Where-Object { $_.Contains($root) }); actionSucceeded=[bool]$d.ActionSuccess } }); ConvertTo-Json -InputObject $hits -Depth 5 -Compress } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 2 }";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(query));
        var powershell = FindRuntimeTool("powershell.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0")) ?? "powershell.exe";
        var start = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not read Microsoft Defender's scan results.");
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
        var output = await stdout;
        if (process.ExitCode != 0) throw new IOException((await stderr).Trim());
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(output) ? "[]" : output);
        var items = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().ToArray()
            : document.RootElement.ValueKind == JsonValueKind.Object ? new[] { document.RootElement } : [];
        var findings = new List<RuntimeSecurityFinding>();
        foreach (var item in items)
        {
            var id = item.TryGetProperty("threatId", out var idNode) ? idNode.ToString() : "Unknown";
            var name = item.TryGetProperty("threatName", out var nameNode) ? nameNode.GetString() ?? "Detected item" : "Detected item";
            var actionSucceeded = item.TryGetProperty("actionSucceeded", out var actionNode) && actionNode.ValueKind == JsonValueKind.True;
            var files = new List<string>();
            if (item.TryGetProperty("resources", out var resources) && resources.ValueKind == JsonValueKind.Array)
            {
                foreach (var resource in resources.EnumerateArray())
                {
                    if (resource.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(resource.GetString())) continue;
                    var fullPath = resource.GetString()!;
                    var rootIndex = fullPath.IndexOf(root, StringComparison.OrdinalIgnoreCase);
                    files.Add(rootIndex >= 0
                        ? Path.GetRelativePath(root, fullPath[(rootIndex + root.Length)..].TrimStart(':', '_', '\\', '/'))
                        : Path.GetFileName(fullPath));
                }
            }
            findings.Add(new RuntimeSecurityFinding(name, id, files.ToArray(), actionSucceeded));
        }
        return findings;
    }

    private async Task<bool> ReviewRuntimeSecurityAsync(string runtimeName, bool scanCompleted,
        string summary, IReadOnlyList<RuntimeSecurityFinding> findings, CancellationToken cancellation)
    {
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _runtimeSecurityDecision = decision;
        _victoryWorldInstallStatus = findings.Count > 0
            ? "Defender reported " + findings.Count + " finding(s). Waiting for your decision…"
            : "Security scan needs review. Waiting for your decision…";
        SendDashboardMessage(new
        {
            type = "runtime-security-review",
            runtimeName,
            scanCompleted,
            summary,
            findings = findings.Select(finding => new
            {
                threatName = finding.ThreatName,
                threatId = finding.ThreatId,
                files = finding.Files,
                actionSucceeded = finding.ActionSucceeded
            }),
            continueLabel = findings.Count > 0 ? "Continue anyway" : "Proceed without a clean scan"
        });
        PublishDashboardState();
        using var stop = cancellation.Register(() => decision.TrySetCanceled(cancellation));
        try
        {
            var proceed = await decision.Task;
            _victoryWorldInstallStatus = proceed
                ? "Security review accepted for " + runtimeName + ". Building…"
                : runtimeName + " installation cancelled after security review.";
            PublishDashboardState();
            return proceed;
        }
        finally
        {
            if (ReferenceEquals(_runtimeSecurityDecision, decision)) _runtimeSecurityDecision = null;
            SendDashboardMessage(new { type = "runtime-security-review-close" });
        }
    }
}
