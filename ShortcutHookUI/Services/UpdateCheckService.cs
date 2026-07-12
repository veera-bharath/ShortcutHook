using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace ShortcutHookUI.Services;

internal static class UpdateCheckService
{
    const string ReleasesApiUrl = "https://api.github.com/repos/veera-bharath/ShortcutHook/releases/latest";

    public readonly record struct UpdateInfo(string Tag, Version Version, string HtmlUrl);

    // Queries the GitHub Releases API for the latest release and returns it if its
    // version is newer than currentVersion. Returns null on any failure (offline,
    // rate-limited, malformed response) or if already up to date — never throws,
    // so callers can fire-and-forget this without delaying startup.
    public static async Task<UpdateInfo?> CheckForUpdateAsync(Version currentVersion)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ShortcutHookUI");

            using var resp = await http.GetAsync(ReleasesApiUrl);
            if (!resp.IsSuccessStatusCode) return null;

            using var stream = await resp.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() : null;
            var url = root.TryGetProperty("html_url", out var urlEl) ? urlEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(url)) return null;

            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;

            return latest > currentVersion ? new UpdateInfo(tag, latest, url) : null;
        }
        catch
        {
            return null;
        }
    }
}
