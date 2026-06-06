using System.Text.RegularExpressions;
using Plexus.Sidecar.Contract;

namespace Plexus.Sidecar.Services;

// Resolves Open Graph metadata (title, description, image) for link_card blocks
// server-side, so the renderer never makes outbound requests. Best-effort: on
// any failure the block keeps whatever the model already provided.
public sealed partial class LinkCardResolver
{
    private readonly HttpClient _http;

    public LinkCardResolver(HttpClient http)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(6);
        if (!_http.DefaultRequestHeaders.UserAgent.TryParseAdd("PlexusBot/0.1 (+https://github.com/plexus)"))
        {
            // ignore parse failure — header is cosmetic
        }
    }

    [GeneratedRegex("""<meta[^>]+(?:property|name)\s*=\s*["']og:(title|description|image)["'][^>]*>""", RegexOptions.IgnoreCase)]
    private static partial Regex OgTagRegex();

    [GeneratedRegex("""content\s*=\s*["']([^"']*)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex ContentAttrRegex();

    [GeneratedRegex("<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleRegex();

    // Enriches every link_card block in the list in place.
    public async Task EnrichAsync(IEnumerable<Block> blocks, CancellationToken ct = default)
    {
        var tasks = blocks.OfType<LinkCardBlock>().Select(b => EnrichOneAsync(b, ct));
        await Task.WhenAll(tasks);
    }

    private async Task EnrichOneAsync(LinkCardBlock block, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(block.Url))
            return;
        if (!Uri.TryCreate(block.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return;

        try
        {
            using var resp = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
                return;
            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (!contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                return;

            var html = await resp.Content.ReadAsStringAsync(ct);
            if (html.Length > 512 * 1024)
                html = html[..(512 * 1024)]; // OG tags live in <head>; cap the read.

            foreach (Match m in OgTagRegex().Matches(html))
            {
                var kind = m.Groups[1].Value.ToLowerInvariant();
                var content = ContentAttrRegex().Match(m.Value).Groups[1].Value;
                if (string.IsNullOrWhiteSpace(content))
                    continue;
                content = System.Net.WebUtility.HtmlDecode(content);
                switch (kind)
                {
                    case "title" when string.IsNullOrEmpty(block.Title): block.Title = content; break;
                    case "description" when string.IsNullOrEmpty(block.Description): block.Description = content; break;
                    case "image" when string.IsNullOrEmpty(block.Image): block.Image = AbsoluteUrl(uri, content); break;
                }
            }

            if (string.IsNullOrEmpty(block.Title))
            {
                var title = TitleRegex().Match(html).Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(title))
                    block.Title = System.Net.WebUtility.HtmlDecode(title);
            }
        }
        catch
        {
            // best-effort — leave the block as-is.
        }
    }

    private static string AbsoluteUrl(Uri pageUri, string maybeRelative)
        => Uri.TryCreate(pageUri, maybeRelative, out var abs) ? abs.ToString() : maybeRelative;
}
