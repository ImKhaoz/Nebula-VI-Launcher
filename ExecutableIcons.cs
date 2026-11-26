using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace XboxEmulatorUI;

internal sealed class ExecutableIcons
{
    private sealed record Entry(long Length, DateTime Modified, string? Data);
    private readonly ConcurrentDictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public string? Read(string path)
    {
        if (!Path.IsPathFullyQualified(path) ||
            !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) { _cache.TryRemove(path, out _); return null; }
            var length = file.Length;
            var modified = file.LastWriteTimeUtc;
            if (_cache.TryGetValue(path, out var entry) && entry.Length == length && entry.Modified == modified)
                return entry.Data;

            var data = Extract(path);
            _cache[path] = new Entry(length, modified, data);
            return data;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or ArgumentException or ExternalException or NotSupportedException)
        {
            return null;
        }
    }

    public void Remove(string path) => _cache.TryRemove(path, out _);

    private static string? Extract(string path)
    {
        const int size = 128;
        var result = SHDefExtractIcon(path, 0, 0, out var icon, nint.Zero, size);
        if (icon == nint.Zero) return null;
        try
        {
            if (result != 0) return null;
            var bitmap = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
        }
        finally { DestroyIcon(icon); }
    }

    [DllImport("shell32.dll", EntryPoint = "SHDefExtractIconW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int SHDefExtractIcon(string path, int index, uint flags, out nint largeIcon, nint smallIcon, uint size);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);
}
