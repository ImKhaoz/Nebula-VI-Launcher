using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XboxEmulatorUI;

internal sealed class SteamCatalog
{
    internal sealed record Card(uint AppId, string Title, string Cover, string Hero, string Header, string? Thumbnail);
    internal sealed record Review(string Excerpt, bool Recommended, long Date);
    internal sealed record News(string Title, string Excerpt, long Date);
    internal sealed record Activity(string? Rating, long ReviewCount, IReadOnlyList<Review> Reviews, IReadOnlyList<News> News);
    internal sealed record Trailer(string Title, string Url, string? Thumbnail, bool Adaptive);
    internal sealed record Media(IReadOnlyList<Trailer> Videos, bool Unavailable);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(7) };
    private readonly SemaphoreSlim _slots = new(3);
    private readonly ConcurrentDictionary<string, Lazy<Task<Card?>>> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (Card? Card, DateTimeOffset Expires)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<uint, Lazy<Task<Activity>>> _activity = new();
    private readonly ConcurrentDictionary<uint, Lazy<Task<Media>>> _media = new();

    public Task<Activity> FindActivityAsync(uint id)
        => _activity.GetOrAdd(id, appId => new Lazy<Task<Activity>>(() => LoadActivityAsync(appId))).Value;

    public async Task<Media> FindMediaAsync(uint id)
    {
        var request = _media.GetOrAdd(id, appId => new Lazy<Task<Media>>(() => ReadMediaAsync(appId)));
        var result = await request.Value;
        if (result.Unavailable) _media.TryRemove(new KeyValuePair<uint, Lazy<Task<Media>>>(id, request));
        return result;
    }

    private static async Task<Media> ReadMediaAsync(uint id)
    {
        var videos = new List<Trailer>();
        try
        {
            using var response = await ReadAsync($"https://store.steampowered.com/api/appdetails?appids={id}&l=english");
            if (!response.RootElement.TryGetProperty(id.ToString(System.Globalization.CultureInfo.InvariantCulture), out var app) ||
                !app.TryGetProperty("data", out var data) || !data.TryGetProperty("movies", out var entries) ||
                entries.ValueKind != JsonValueKind.Array) return new Media(videos, false);

            foreach (var item in entries.EnumerateArray())
            {
                var adaptive = ReadText(item, "hls_h264");
                var progressive = MovieFile(item, "mp4");
                if (!IsSteamMedia(progressive, ".mp4")) progressive = MovieFile(item, "webm");
                var useAdaptive = IsSteamMedia(adaptive, ".m3u8");
                if (!useAdaptive && !IsSteamMedia(progressive, ".webm") && !IsSteamMedia(progressive, ".mp4")) continue;

                var title = ReadText(item, "name");
                if (string.IsNullOrWhiteSpace(title)) title = "Steam trailer";
                var thumbnail = ReadText(item, "thumbnail");
                videos.Add(new Trailer(title, useAdaptive ? adaptive : progressive,
                    IsSteamArt(thumbnail) ? thumbnail : null, useAdaptive));
                if (videos.Count >= 12) break;
            }
            return new Media(videos, false);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return new Media(videos, true);
        }
    }

    private static string MovieFile(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var sources) || sources.ValueKind != JsonValueKind.Object) return "";
        var standard = ReadText(sources, "480");
        return standard.Length != 0 ? standard : ReadText(sources, "max");
    }

    private static bool IsSteamMedia(string value, string extension)
        => IsSteamArt(value) && new Uri(value).AbsolutePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase);

    public async Task<Card?> FindAsync(string title, string executablePath)
    {
        if (_cache.TryGetValue(executablePath, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
            return cached.Card;
        var lookup = _pending.GetOrAdd(executablePath, _ => new Lazy<Task<Card?>>(() => LookupAsync(title, executablePath)));
        try
        {
            var card = await lookup.Value;
            _cache[executablePath] = (card, DateTimeOffset.UtcNow.AddMinutes(card is null ? 15 : 180));
            return card;
        }
        finally { _pending.TryRemove(new KeyValuePair<string, Lazy<Task<Card?>>>(executablePath, lookup)); }
    }

    private async Task<Card?> LookupAsync(string title, string path)
    {
        await _slots.WaitAsync();
        try
        {
            var product = await Task.Run(() => ReadProductName(path));
            var terms = new[] { title, product }.Where(term => !string.IsNullOrWhiteSpace(term)).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var term in terms)
            {
                var card = await SearchAsync(term!);
                if (card is not null) return card;
            }
            return null;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
        finally { _slots.Release(); }
    }

    private static async Task<Card?> SearchAsync(string term)
    {
        using var search = await ReadAsync($"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(term)}&l=english&cc=us");
        if (!search.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return null;
        var match = items.EnumerateArray().FirstOrDefault(item =>
            item.TryGetProperty("type", out var type) && type.GetString() == "app" &&
            item.TryGetProperty("name", out var name) && Normalize(name.GetString() ?? "") == Normalize(term));
        if (match.ValueKind == JsonValueKind.Undefined || !match.GetProperty("id").TryGetUInt32(out var id)) return null;
        var title = match.GetProperty("name").GetString() ?? term;
        var thumbnail = match.TryGetProperty("tiny_image", out var image) && IsSteamArt(image.GetString())
            ? image.GetString() : null;
        var assets = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{id}";
        return new Card(id, title, $"{assets}/library_600x900.jpg", $"{assets}/library_hero.jpg",
            $"{assets}/header.jpg", thumbnail);
    }

    private static async Task<Activity> LoadActivityAsync(uint id)
    {
        var reviewsRequest = ReadReviewsAsync(id);
        var newsRequest = ReadNewsAsync(id);
        var (rating, count, reviews) = await reviewsRequest;
        return new Activity(rating, count, reviews, await newsRequest);
    }

    private static async Task<(string? Rating, long Count, IReadOnlyList<Review> Reviews)> ReadReviewsAsync(uint id)
    {
        string? rating = null;
        long count = 0;
        var items = new List<Review>();
        try
        {
            var request = Uri.EscapeDataString(JsonSerializer.Serialize(new { appid = id, num_per_page = 4 }));
            using var reviews = await ReadAsync($"https://api.steampowered.com/IUserReviewsService/GetAppReviews/v1/?input_json={request}");
            if (!reviews.RootElement.TryGetProperty("response", out var response)) return (null, 0, items);
            if (response.TryGetProperty("query_summary", out var summary))
            {
                if (summary.TryGetProperty("review_score_desc", out var description)) rating = description.GetString();
                if (summary.TryGetProperty("total_reviews", out var total)) total.TryGetInt64(out count);
            }
            if (response.TryGetProperty("reviews", out var reviewItems) && reviewItems.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in reviewItems.EnumerateArray())
                {
                    var text = Excerpt(ReadText(item, "review"));
                    if (text.Length == 0) continue;
                    var recommended = item.TryGetProperty("voted_up", out var voted) && voted.ValueKind == JsonValueKind.True;
                    items.Add(new Review(text, recommended, ReadDate(item, "timestamp_created")));
                    if (items.Count == 2) break;
                }
            }
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException) { }
        return (rating, count, items);
    }

    private static async Task<IReadOnlyList<News>> ReadNewsAsync(uint id)
    {
        var items = new List<News>();
        try
        {
            using var news = await ReadAsync($"https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/?appid={id}&count=3&maxlength=400&feeds=steam_community_announcements");
            if (!news.RootElement.TryGetProperty("appnews", out var appNews) ||
                !appNews.TryGetProperty("newsitems", out var entries) || entries.ValueKind != JsonValueKind.Array) return items;
            foreach (var item in entries.EnumerateArray())
            {
                var title = Excerpt(ReadText(item, "title"));
                if (title.Length == 0) continue;
                items.Add(new News(title, Excerpt(ReadText(item, "contents")), ReadDate(item, "date")));
                if (items.Count == 2) break;
            }
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException) { }
        return items;
    }

    private static string ReadText(JsonElement item, string key)
        => item.TryGetProperty(key, out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() ?? "" : "";

    private static long ReadDate(JsonElement item, string key)
        => item.TryGetProperty(key, out var date) && date.TryGetInt64(out var value) ? value : 0;

    private static string Excerpt(string text)
    {
        if (text.Length > 3000) text = text[..3000];
        var clean = Regex.Replace(WebUtility.HtmlDecode(text), @"<[^>]*>|\[/?[^\]]*\]|\{STEAM_CLAN_IMAGE\}\S*", " ");
        var words = Regex.Split(clean.Trim(), @"\s+");
        var excerpt = string.Join(" ", words.Take(22));
        if (excerpt.Length > 205) return excerpt[..205].TrimEnd() + "…";
        return words.Length > 22 ? excerpt + "…" : excerpt;
    }

    private static string? ReadProductName(string path)
    {
        try
        {
            var name = FileVersionInfo.GetVersionInfo(path).ProductName;
            return name is { Length: > 1 and <= 110 } ? name : null;
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or ArgumentException) { return null; }
    }

    private static async Task<JsonDocument> ReadAsync(string url)
    {
        using var response = await Http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }

    private static string Normalize(string name)
        => string.Concat(name.ToLowerInvariant().Where(char.IsLetterOrDigit));

    private static bool IsSteamArt(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
           uri.Host.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase);
}
