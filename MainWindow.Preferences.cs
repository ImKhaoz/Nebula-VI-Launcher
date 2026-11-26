using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace XboxEmulatorUI;

public partial class MainWindow
{
    private void SaveDashboardPreferences(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return;
        var previous = (_startupSound, _showIntro, _recordingDelay, _backgroundMotion, _videoPreview, _defaultLayout);
        _startupSound = ReadPreference(value, "startupSound", _startupSound);
        _showIntro = ReadPreference(value, "showIntro", _showIntro);
        _recordingDelay = ReadPreference(value, "recordingDelay", _recordingDelay);
        _backgroundMotion = ReadPreference(value, "backgroundMotion", _backgroundMotion);
        _videoPreview = ReadPreference(value, "videoPreview", _videoPreview);
        var layout = ReadOption(value, "layout");
        if (layout is "grid" or "carousel") _defaultLayout = layout;
        try { SaveSettings(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            (_startupSound, _showIntro, _recordingDelay, _backgroundMotion, _videoPreview, _defaultLayout) = previous;
            throw;
        }
        PublishDashboardState();
        SendDashboardMessage(new { type = "preferences-saved" });
        ShowToast("Launcher preferences saved.");
    }

    private void PublishGameProperties(GameEntry game)
        => SendDashboardMessage(new
        {
            type = "game-properties", id = game.Id,
            alias = game.DisplayName ?? "", defaultName = game.Name,
            args = game.LaunchArguments ?? "", xbox = game.LaunchKind == "XValKyrie",
            leonida = game.IsLeonida, executableLinked = !string.IsNullOrWhiteSpace(game.ExecutablePath)
        });

    private void SaveGameProperties(GameEntry game, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return;
        var name = ReadOption(value, "displayName").Trim();
        var arguments = ReadOption(value, "arguments").Trim();
        if (name.Length > 100 || arguments.Length > 1500 || name.Any(char.IsControl) || arguments.Any(char.IsControl))
        {
            ShowToast("Use a name of up to 100 characters and a single line of launch options.");
            return;
        }

        var previous = (game.DisplayName, game.LaunchArguments);
        game.DisplayName = name.Length == 0 ? null : name;
        if (!game.IsLeonida && game.LaunchKind != "XValKyrie") game.LaunchArguments = arguments.Length == 0 ? null : arguments;
        try { SaveLibrary(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            (game.DisplayName, game.LaunchArguments) = previous;
            throw;
        }
        RefreshLibrary();
        SendDashboardMessage(new { type = "game-properties-saved", id = game.Id });
        ShowToast("Game settings saved.");
    }

    private void OpenGameFolder(GameEntry game)
    {
        var directory = Path.GetDirectoryName(game.ExecutablePath);
        if (game.IsLeonida && string.IsNullOrEmpty(directory)) directory = _leonidaGameFolder;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("The game’s folder could not be found.");
        Process.Start(new ProcessStartInfo(directory!) { UseShellExecute = true })?.Dispose();
    }

    private static string ReadOption(JsonElement value, string name)
        => value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : "";

    private static bool ReadPreference(JsonElement value, string name, bool fallback)
        => value.TryGetProperty(name, out var field) && field.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? field.GetBoolean() : fallback;
}
