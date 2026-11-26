using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace XboxEmulatorUI;

public partial class DiagnosticsWindow : Window
{
    private readonly CancellationTokenSource _closed = new();
    private readonly string? _runtimePath;

    public DiagnosticsWindow(string? runtimePath)
    {
        _runtimePath = runtimePath;
        InitializeComponent();
        WindowAppearance.UseDarkFrame(this);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed.Cancel();
        base.OnClosed(e);
    }

    private async void Guest_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (_runtimePath is null) throw new InvalidOperationException("Optional Xbox compatibility files are not configured.");
        var boot = Path.Combine(Path.GetDirectoryName(_runtimePath)!, "xvboot.exe");
        var guest = Path.Combine(AppContext.BaseDirectory, "diagnostics", "ScarlettSmoke.exe");
        RequireFile(guest);
        var code = await InvokeAsync(boot, 30, "exec", guest);
        return code == 42
            ? "PASS · Series guest created and released a Direct3D 12 device"
            : $"FAIL · Guest returned 0x{code:X8}, expected 42";
    });

    private async void Gpu_Click(object sender, RoutedEventArgs e) => await RunAsync(() => CheckGraphicsAsync("hardware"));
    private async void Warp_Click(object sender, RoutedEventArgs e) => await RunAsync(() => CheckGraphicsAsync("warp"));

    private async Task<string> CheckGraphicsAsync(string backend)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "diagnostics", "Xbox2Windows", "offscreen_check.exe");
        RequireFile(exe);
        int passes = 0, skips = 0, failures = 0;
        foreach (var name in new[] { "waves", "wave64", "differential" })
        {
            var code = await InvokeAsync(exe, 60, backend, $"--bvh-{name}");
            Output.AppendText($"{name}: {(code == 0 ? "PASS" : code == 77 ? "SKIP" : $"FAIL (0x{code:X8})")}\n\n");
            if (code == 0) passes++;
            else if (code == 77) skips++;
            else failures++;
        }
        var state = failures != 0 ? "FAIL" : passes != 0 ? "PASS" : "SKIP";
        return $"{state} · {passes} passed, {skips} skipped, {failures} failed";
    }

    private async void Recomp_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var source = Path.Combine(directory.FullName, "xbox2windows-upstream");
            var build = Path.Combine(source, "out", "build", "dev");
            var ctest = Path.Combine(source, "out", "pe-env", "Scripts", "ctest.exe");
            if (!File.Exists(ctest) || !File.Exists(Path.Combine(build, "CTestTestfile.cmake"))) continue;
            var code = await InvokeAsync(ctest, 180, "--test-dir", build, "--parallel", "1", "--no-tests=error",
                "-R", "^(cpu_native_startup|compute_store_aot_warp|pixel_aot_warp|mesh_indexed_draw_warp)$", "-V");
            if (code != 0) return $"FAIL · CTest exited with code {code}";
            return Output.Text.Contains("***Skipped", StringComparison.Ordinal)
                ? "COMPLETE · CTest reported capability skips"
                : "PASS · CPU recompilation and shader fixtures";
        }
        throw new InvalidOperationException("The Xbox2Windows source build was not found. Build the dev preset using the README.");
    });

    private async Task RunAsync(Func<Task<string>> check)
    {
        Actions.IsEnabled = false;
        Output.Clear();
        Result.Text = "Running…";
        try { Result.Text = await check(); }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested) { }
        catch (Exception error) { Result.Text = "Check did not complete"; Output.AppendText(error.Message); }
        finally { Actions.IsEnabled = true; }
    }

    private async Task<int> InvokeAsync(string executable, int seconds, params string[] arguments)
    {
        RequireFile(executable);
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        Output.AppendText($"> {Path.GetFileName(executable)} {string.Join(" ", arguments)}\n");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
        limit.CancelAfter(TimeSpan.FromSeconds(seconds));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the check.");
        var stdout = process.StandardOutput.ReadToEndAsync(limit.Token);
        var stderr = process.StandardError.ReadToEndAsync(limit.Token);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(limit.Token), stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            if (_closed.IsCancellationRequested) throw;
            throw new TimeoutException($"The check exceeded {seconds} seconds.");
        }
        Output.AppendText(await stdout);
        Output.AppendText(await stderr);
        Output.ScrollToEnd();
        return process.ExitCode;
    }

    private static void RequireFile(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Required diagnostic file is missing: {path}");
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { FileName = "Nebula-diagnostics.txt", Filter = "Text files (*.txt)|*.txt" };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, $"{DateTimeOffset.Now:O}\n{Result.Text}\n\n{Output.Text}"); }
        catch (Exception error) { MessageBox.Show(this, error.Message, "Could not save report"); }
    }
}
