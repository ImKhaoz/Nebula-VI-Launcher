using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace XboxEmulatorUI;

internal sealed class GtaActivityFeed
{
    public sealed record Article(string Title, string Url, string? Date = null, string? Summary = null);
    public sealed record Feed(IReadOnlyList<Article> News, IReadOnlyList<Article> Tips, bool NewsLive, bool TipsLive);

    private const string NewsHub = "https://www.rockstargames.com/VI";
    private const string TipsHub = "https://www.gamesradar.com/games/grand-theft-auto/gta-guides-walkthroughs/";
    private const string GuideBase = "https://www.gamesradar.com/games/grand-theft-auto/";
    private static readonly HttpClient Http = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
    {
        Timeout = TimeSpan.FromSeconds(8),
        MaxResponseContentBufferSize = 8 * 1024 * 1024
    };
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);
    private static readonly Regex Anchors = new(
        """<a\b(?<attributes>[^>]*)\shref=["'](?<href>[^"']+)["'](?<tail>[^>]*)>(?<body>.*?)</a>""",
        RegexOptions.Singleline | RegexOptions.IgnoreCase, MatchTimeout);
    private static readonly Regex NewsTitle = new(@"<h4\b[^>]*>(?<value>.*?)</h4>", RegexOptions.Singleline, MatchTimeout);
    private static readonly Regex NewsDate = new(@"<p\b[^>]*>(?<value>.*?)</p>", RegexOptions.Singleline, MatchTimeout);
    private static readonly Regex GuideTitle = new(@"<h3\b[^>]*>(?<value>.*?)</h3>", RegexOptions.Singleline, MatchTimeout);
    private static readonly Regex Tags = new("<[^>]+>", RegexOptions.None, MatchTimeout);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.None, MatchTimeout);

    private static readonly Article[] SavedNews =
    [
        new("Grand Theft Auto VI is out now", "https://www.rockstargames.com/newswire/article/39akk424a9oo53", "2026-11-19", "Rockstar's launch announcement for Xbox Series X|S and PlayStation 5."),
        new("Explore the GTA VI launch gallery", "https://www.rockstargames.com/newswire/article/39ak81277255o9", "2026-11-05", "See official screenshots from around Leonida.")
    ];
    private static readonly Article[] SavedTips =
    [
        new("How to save your progress", GuideBase + "gta-6-how-to-save/", Summary: "Find out how autosaves work and how to save manually."),
        new("Find your way around Leonida", "https://www.gamesradar.com/gta-6-map/", Summary: "Explore the map for major regions and points of interest."),
        new("How to fast travel", GuideBase + "gta-6-how-to-fast-travel/", Summary: "Learn which travel options help you cross the map faster.")
    ];

    private readonly SemaphoreSlim _refresh = new(1);
    private DateTimeOffset _refreshAfter;
    private Feed _current = new(SavedNews, SavedTips, false, false);

    public async Task<Feed> GetAsync()
    {
        await _refresh.WaitAsync();
        try
        {
            if (DateTimeOffset.UtcNow < _refreshAfter) return _current;
            var newsRequest = ReadAsync(NewsHub, news: true);
            var tipsRequest = ReadAsync(TipsHub, news: false);
            await Task.WhenAll(newsRequest, tipsRequest);
            var news = await newsRequest;
            var tips = await tipsRequest;
            _current = new(news ?? _current.News, tips ?? _current.Tips, news is not null, tips is not null);
            _refreshAfter = DateTimeOffset.UtcNow.AddMinutes(_current.NewsLive && _current.TipsLive ? 15 : 2);
            return _current;
        }
        finally { _refresh.Release(); }
    }

    public bool CanOpen(string url)
        => url is NewsHub or TipsHub || _current.News.Concat(_current.Tips).Any(article => article.Url == url);

    private static async Task<IReadOnlyList<Article>?> ReadAsync(string hub, bool news)
    {
        try
        {
            var page = new Uri(hub);
            var html = await Http.GetStringAsync(page);
            var articles = new List<Article>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match anchor in Anchors.Matches(html))
            {
                if (!Uri.TryCreate(page, WebUtility.HtmlDecode(anchor.Groups["href"].Value), out var uri) ||
                    uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
                    !string.Equals(uri.Host, page.Host, StringComparison.OrdinalIgnoreCase)) continue;
                if (news ? !uri.AbsolutePath.StartsWith("/newswire/article/", StringComparison.Ordinal)
                    : !anchor.Groups["tail"].Value.Contains("data-gta-6-guide-event=", StringComparison.OrdinalIgnoreCase) &&
                      !anchor.Groups["attributes"].Value.Contains("data-gta-6-guide-event=", StringComparison.OrdinalIgnoreCase)) continue;

                var body = anchor.Groups["body"].Value;
                var title = ReadText((news ? NewsTitle : GuideTitle).Match(body));
                if (title.Length is 0 or > 180) continue;
                var url = uri.GetLeftPart(UriPartial.Path);
                if (!seen.Add(url)) continue;
                var date = news && DateOnly.TryParseExact(ReadText(NewsDate.Match(body)), "MMMM d, yyyy",
                    CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None, out var published)
                    ? published.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
                var saved = (news ? SavedNews : SavedTips).FirstOrDefault(article => article.Url == url);
                articles.Add(new Article(title, url, date, saved?.Summary));
                if (!news && articles.Count == 5) break;
            }
            if (articles.Count == 0) return null;
            return news ? articles.OrderByDescending(article => article.Date, StringComparer.Ordinal).Take(4).ToArray() : articles;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private static string ReadText(Match tag)
        => Spaces.Replace(WebUtility.HtmlDecode(Tags.Replace(tag.Groups["value"].Value, " ")), " ").Trim();
}
