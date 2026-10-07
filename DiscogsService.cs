using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AvaloniaCDInventory;

public class DiscogsService
{
    private const string UserAgent = "MyCDScanner/TriMode/1.0";

    // The personal access token is kept out of source control. It is read from the DISCOGS_TOKEN
    // environment variable, or from %APPDATA%\AvaloniaCDInventory\discogs_token.txt.
    public static readonly string TokenFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AvaloniaCDInventory", "discogs_token.txt");

    private readonly HttpClient _httpClient;
    private readonly string? _apiToken;

    public DiscogsService()
    {
        _apiToken = LoadToken();
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        if (_apiToken != null)
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Discogs token={_apiToken}");
    }

    private static string? LoadToken()
    {
        string? token = Environment.GetEnvironmentVariable("DISCOGS_TOKEN");
        if (string.IsNullOrWhiteSpace(token) && File.Exists(TokenFilePath))
            token = File.ReadAllText(TokenFilePath);
        return string.IsNullOrWhiteSpace(token) ? null : token.Trim();
    }

    public Task<Album?> SearchAlbumByBarcodeAsync(string upc) => FetchAndMapReleaseAsync("barcode", upc);

    public Task<Album?> SearchAlbumByCatalogIdAsync(string catId) => FetchAndMapReleaseAsync("catno", catId);

    // Returns null when Discogs has no match; network/auth/rate-limit failures throw so the caller can report them
    private async Task<Album?> FetchAndMapReleaseAsync(string searchField, string databaseId)
    {
        if (_apiToken == null)
            throw new InvalidOperationException($"No Discogs token found. Put your token in {TokenFilePath}");

        string searchUrl = $"https://api.discogs.com/database/search?{searchField}={Uri.EscapeDataString(databaseId)}&type=release";
        var searchResponse = await _httpClient.GetStringAsync(searchUrl);
        using var searchDoc = JsonDocument.Parse(searchResponse);
        
        var results = searchDoc.RootElement.GetProperty("results");
        if (results.GetArrayLength() == 0) return null; 

        int releaseId = results[0].GetProperty("id").GetInt32();
        string releaseUrl = $"https://api.discogs.com/releases/{releaseId}";
        var releaseResponse = await _httpClient.GetStringAsync(releaseUrl);
        using var releaseDoc = JsonDocument.Parse(releaseResponse);
        var root = releaseDoc.RootElement;

        // Map ID to the UPC property to satisfy the SQLite database structure
        var album = new Album
        {
            UPC = databaseId,
            Title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
            Year = root.TryGetProperty("year", out var y) ? y.GetInt32().ToString() : ""
        };

        if (root.TryGetProperty("artists", out var artists) && artists.GetArrayLength() > 0)
        {
            album.Artist = artists[0].GetProperty("name").GetString() ?? "";
            album.SortArtist = GenerateSortArtist(album.Artist);
        }

        if (root.TryGetProperty("formats", out var formats) && formats.GetArrayLength() > 0)
        {
            album.Format = formats[0].GetProperty("name").GetString() ?? "Unknown";
        }

        if (root.TryGetProperty("labels", out var labels) && labels.GetArrayLength() > 0)
        {
            album.Label = labels[0].GetProperty("name").GetString() ?? "Unknown";
        }

        if (root.TryGetProperty("genres", out var genres))
        {
            album.Genre = string.Join(", ", genres.EnumerateArray().Select(g => g.GetString()));
        }

        if (root.TryGetProperty("styles", out var styles))
        {
            album.Styles = string.Join(", ", styles.EnumerateArray().Select(s => s.GetString()));
        }

        if (root.TryGetProperty("tracklist", out var tracklist))
        {
            foreach (var trackElement in tracklist.EnumerateArray())
            {
                var track = new Track
                {
                    Position = trackElement.TryGetProperty("position", out var pos) ? pos.GetString() ?? "" : "",
                    TrackTitle = trackElement.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
                    Duration = trackElement.TryGetProperty("duration", out var dur) ? dur.GetString() ?? "" : ""
                };
                album.Tracks.Add(track);
            }
        }

        return album;
    }

    private string GenerateSortArtist(string artistName)
    {
        if (string.IsNullOrWhiteSpace(artistName)) return "";
        string cleanName = Regex.Replace(artistName, @"\s\(\d+\)$", "").Trim();
        string lowerName = cleanName.ToLower();
        if (lowerName.StartsWith("the ")) return cleanName.Substring(4) + ", The";
        if (lowerName.StartsWith("a ")) return cleanName.Substring(2) + ", A";
        if (lowerName.StartsWith("an ")) return cleanName.Substring(3) + ", An";
        return cleanName;
    }
}