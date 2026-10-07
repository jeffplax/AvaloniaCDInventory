using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data.SQLite;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AvaloniaCDInventory;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly DiscogsService _discogs = new();

    // Columns the search filter is allowed to target (guards the dynamic SQL below)
    private static readonly HashSet<string> FilterColumns = new() { "Artist", "Title", "Year", "Format", "Label", "Genre" };

    [ObservableProperty]
    private string _connectionString = string.Empty;

    [ObservableProperty]
    private string _wishlistConnectionString = string.Empty;

    public ObservableCollection<Album> AllAlbums { get; } = new();
    public ObservableCollection<WishListItem> WishlistItems { get; } = new();

    // Bound to the DataGrid's SelectedItem. The view scrolls to it whenever it changes.
    [ObservableProperty] private Album? _selectedAlbum;

    [ObservableProperty] private string _statusText = "DB not connected.";
    [ObservableProperty] private string _albumCountText = "0 Albums";

    // Wishlist UI Bindings
    [ObservableProperty] private string _wishArtist = "";
    [ObservableProperty] private string _wishTitle = "";
    [ObservableProperty] private string _wishFormat = "CD";
    [ObservableProperty] private string _wishNotes = "";

    // Search & Filter Bindings
    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private string _selectedFilter = "All";
    [ObservableProperty] private string _manualEntryText = "";

    // Called automatically when search text or filter changes!
    partial void OnSearchQueryChanged(string value) => LoadMasterLibrary();
    partial void OnSelectedFilterChanged(string value) => LoadMasterLibrary();

    public void InitializeConnections(string dbPath, string wishlistPath)
    {
        ConnectionString = $"Data Source={dbPath};Version=3;";
        WishlistConnectionString = $"Data Source={wishlistPath};Version=3;";

        LoadWishlist();
        LoadMasterLibrary();
    }

    [RelayCommand]
    public void LoadMasterLibrary()
    {
        if (string.IsNullOrEmpty(ConnectionString)) return;

        // Remember the selection so a reload doesn't throw the user back to the top of the grid
        string? selectedUpc = SelectedAlbum?.UPC;

        try
        {
            AllAlbums.Clear();
            using var conn = new SQLiteConnection(ConnectionString);
            conn.Open();

            var allTracks = new Dictionary<string, List<Track>>();
            using (var trackCmd = new SQLiteCommand("SELECT UPC, Position, Track_Title, Duration FROM tracks ORDER BY TrackID", conn))
            using (var trackReader = trackCmd.ExecuteReader())
            {
                while (trackReader.Read())
                {
                    string upc = trackReader["UPC"]?.ToString() ?? "";
                    if (!allTracks.ContainsKey(upc)) allTracks[upc] = new();

                    allTracks[upc].Add(new Track {
                        Position = trackReader["Position"]?.ToString() ?? "",
                        TrackTitle = trackReader["Track_Title"]?.ToString() ?? "",
                        Duration = trackReader["Duration"]?.ToString() ?? ""
                    });
                }
            }

            string sql = "SELECT * FROM albums";
            if (!string.IsNullOrWhiteSpace(SearchQuery))
            {
                if (FilterColumns.Contains(SelectedFilter))
                    sql += $" WHERE {SelectedFilter} LIKE @q";
                else
                    sql += " WHERE Artist LIKE @q OR Title LIKE @q OR Genre LIKE @q";
            }

            using var cmd = new SQLiteCommand(sql, conn);
            if (!string.IsNullOrWhiteSpace(SearchQuery))
                cmd.Parameters.AddWithValue("@q", $"%{SearchQuery}%");

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string currentUpc = r["UPC"].ToString() ?? "";
                var newAlbum = new Album {
                    UPC = currentUpc,
                    Artist = r["Artist"].ToString() ?? "",
                    SortArtist = r["SortArtist"]?.ToString() ?? "",
                    Title = r["Title"].ToString() ?? "",
                    Year = r["Year"].ToString() ?? "",
                    Format = r["Format"].ToString() ?? "",
                    Label = r["Label"].ToString() ?? "",
                    Genre = r["Genre"]?.ToString() ?? "",
                    Styles = r["Styles"]?.ToString() ?? ""
                };

                if (allTracks.ContainsKey(currentUpc))
                    newAlbum.Tracks = new ObservableCollection<Track>(allTracks[currentUpc]);

                AllAlbums.Add(newAlbum);
            }
            AlbumCountText = $"{AllAlbums.Count} Albums";
            SelectedAlbum = AllAlbums.FirstOrDefault(a => a.UPC == selectedUpc);
        }
        catch (Exception ex)
        {
            StatusText = $"Database Error: {ex.Message}";
        }
    }

    // =====================
    // Wishlist
    // =====================
    // wishlist.db is shared with the mobile app, which merges it row-by-row (see MobileCDInventory's
    // WishlistSync). To make that merge possible, rows are never hard-deleted: a delete sets Deleted = 1
    // and every change stamps Modified (UTC), so the newest version of an Artist + Title wins.

    [RelayCommand]
    public void LoadWishlist()
    {
        if (string.IsNullOrEmpty(WishlistConnectionString)) return;

        try
        {
            WishlistItems.Clear();
            using var conn = new SQLiteConnection(WishlistConnectionString);
            conn.Open();
            EnsureWishlistSchema(conn);

            using var cmd = new SQLiteCommand("SELECT * FROM wishlist WHERE Deleted = 0 ORDER BY DateAdded DESC", conn);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                WishlistItems.Add(new WishListItem
                {
                    WishID = Convert.ToInt32(reader["WishID"]),
                    Artist = reader["Artist"]?.ToString() ?? "",
                    Title = reader["Title"]?.ToString() ?? "",
                    Format = reader["Format"]?.ToString() ?? "",
                    Notes = reader["Notes"]?.ToString() ?? "",
                    DateAdded = reader["DateAdded"]?.ToString() ?? ""
                });
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Error loading wishlist: {ex.Message}";
        }
    }

    private static void EnsureWishlistSchema(SQLiteConnection conn)
    {
        using (var createCmd = new SQLiteCommand(@"CREATE TABLE IF NOT EXISTS wishlist (
                                    WishID INTEGER PRIMARY KEY AUTOINCREMENT,
                                    Artist TEXT,
                                    Title TEXT NOT NULL,
                                    Format TEXT,
                                    Notes TEXT,
                                    DateAdded TEXT,
                                    Modified TEXT,
                                    Deleted INTEGER NOT NULL DEFAULT 0
                                )", conn))
        {
            createCmd.ExecuteNonQuery();
        }

        // Upgrade wishlists created before sync support existed
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var infoCmd = new SQLiteCommand("PRAGMA table_info(wishlist)", conn))
        using (var info = infoCmd.ExecuteReader())
        {
            while (info.Read()) columns.Add(info["name"].ToString() ?? "");
        }

        if (!columns.Contains("Modified"))
            Execute(conn, "ALTER TABLE wishlist ADD COLUMN Modified TEXT");
        if (!columns.Contains("Deleted"))
            Execute(conn, "ALTER TABLE wishlist ADD COLUMN Deleted INTEGER NOT NULL DEFAULT 0");

        Execute(conn, "UPDATE wishlist SET Modified = COALESCE(DateAdded, '2000-01-01') || 'T00:00:00.000Z' WHERE Modified IS NULL");
    }

    private static void Execute(SQLiteConnection conn, string sql)
    {
        using var cmd = new SQLiteCommand(sql, conn);
        cmd.ExecuteNonQuery();
    }

    private static string UtcStamp() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

    [RelayCommand]
    private void SaveWish()
    {
        if (string.IsNullOrEmpty(WishlistConnectionString))
        {
            StatusText = "Error: Connect to the Master Library DB first.";
            return;
        }

        if (string.IsNullOrWhiteSpace(WishTitle))
        {
            StatusText = "Error: Wishlist Title is required.";
            return;
        }
        try
        {
            using var conn = new SQLiteConnection(WishlistConnectionString);
            conn.Open();
            EnsureWishlistSchema(conn);

            string artist = (WishArtist ?? "").Trim();
            string title = WishTitle.Trim();

            // Re-adding a previously deleted (or existing) target revives that row instead of duplicating it
            using var cmd = new SQLiteCommand(@"UPDATE wishlist
                                                SET Format = @format, Notes = @notes, Deleted = 0, Modified = @modified
                                                WHERE lower(trim(Artist)) = lower(@artist) AND lower(trim(Title)) = lower(@title)", conn);
            cmd.Parameters.AddWithValue("@artist", artist);
            cmd.Parameters.AddWithValue("@title", title);
            cmd.Parameters.AddWithValue("@format", WishFormat ?? "");
            cmd.Parameters.AddWithValue("@notes", WishNotes ?? "");
            cmd.Parameters.AddWithValue("@modified", UtcStamp());

            if (cmd.ExecuteNonQuery() == 0)
            {
                cmd.CommandText = @"INSERT INTO wishlist (Artist, Title, Format, Notes, DateAdded, Modified, Deleted)
                                    VALUES (@artist, @title, @format, @notes, @dateAdded, @modified, 0)";
                cmd.Parameters.AddWithValue("@dateAdded", DateTime.Now.ToString("yyyy-MM-dd"));
                cmd.ExecuteNonQuery();
            }

            WishArtist = ""; WishTitle = ""; WishFormat = "CD"; WishNotes = "";
            StatusText = "Target added to Wish List.";
            LoadWishlist();
        }
        catch (Exception ex)
        {
            StatusText = $"Error saving to wishlist: {ex.Message}";
        }
    }

    [RelayCommand]
    public void DeleteWishlistItem(WishListItem item)
    {
        if (item == null || string.IsNullOrEmpty(WishlistConnectionString)) return;
        try
        {
            using var conn = new SQLiteConnection(WishlistConnectionString);
            conn.Open();
            // Match on Artist + Title rather than WishID: IDs are local to each copy of the file
            using var cmd = new SQLiteCommand("UPDATE wishlist SET Deleted = 1, Modified = @modified WHERE Artist = @artist AND Title = @title", conn);
            cmd.Parameters.AddWithValue("@modified", UtcStamp());
            cmd.Parameters.AddWithValue("@artist", item.Artist);
            cmd.Parameters.AddWithValue("@title", item.Title);
            cmd.ExecuteNonQuery();

            WishlistItems.Remove(item);
            StatusText = $"Deleted target: {item.Title}";
        }
        catch (Exception ex) { StatusText = $"Error deleting wishlist item: {ex.Message}"; }
    }

    // =====================
    // Master Library edits
    // =====================

    [RelayCommand]
    public void DeleteAlbum(Album album)
    {
        if (album == null || string.IsNullOrEmpty(ConnectionString)) return;
        try
        {
            using var conn = new SQLiteConnection(ConnectionString);
            conn.Open();
            using var tx = conn.BeginTransaction();

            // SQLite doesn't enforce the ON DELETE CASCADE unless foreign_keys is on, so remove the tracks
            // explicitly; otherwise re-scanning the CD later would show its tracklist twice.
            using (var tCmd = new SQLiteCommand("DELETE FROM tracks WHERE UPC = @upc", conn, tx))
            {
                tCmd.Parameters.AddWithValue("@upc", album.UPC);
                tCmd.ExecuteNonQuery();
            }
            using (var cmd = new SQLiteCommand("DELETE FROM albums WHERE UPC = @upc", conn, tx))
            {
                cmd.Parameters.AddWithValue("@upc", album.UPC);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();

            AllAlbums.Remove(album);
            AlbumCountText = $"{AllAlbums.Count} Albums";
            StatusText = $"Deleted: {album.Title}";
        }
        catch (Exception ex) { StatusText = $"Error deleting album: {ex.Message}"; }
    }

    [RelayCommand]
    public async Task SearchManualEntryAsync()
    {
        if (!string.IsNullOrWhiteSpace(ManualEntryText))
        {
            await ProcessBarcodeAsync(ManualEntryText.Trim());
            ManualEntryText = ""; // Clear the text box after searching
        }
    }

    public void UpdateAlbum(Album album)
    {
         if (album == null || string.IsNullOrEmpty(ConnectionString)) return;
         try
         {
             using var conn = new SQLiteConnection(ConnectionString);
             conn.Open();
             string sql = @"UPDATE albums
                            SET Artist = @artist, SortArtist = @sortArtist, Title = @title, Year = @year,
                                Format = @format, Label = @label, Genre = @genre, Styles = @styles
                            WHERE UPC = @upc";

             using var cmd = new SQLiteCommand(sql, conn);
             cmd.Parameters.AddWithValue("@artist", album.Artist);
             cmd.Parameters.AddWithValue("@sortArtist", album.SortArtist);
             cmd.Parameters.AddWithValue("@title", album.Title);
             cmd.Parameters.AddWithValue("@year", album.Year);
             cmd.Parameters.AddWithValue("@format", album.Format);
             cmd.Parameters.AddWithValue("@label", album.Label);
             cmd.Parameters.AddWithValue("@genre", album.Genre);
             cmd.Parameters.AddWithValue("@styles", album.Styles);
             cmd.Parameters.AddWithValue("@upc", album.UPC);
             cmd.ExecuteNonQuery();

             StatusText = $"Saved edit: {album.Title}";
         }
         catch (Exception ex)
         {
             StatusText = $"Failed to save edit: {ex.Message}";
             LoadMasterLibrary();
         }
    }

    public async Task ProcessBarcodeAsync(string input)
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            StatusText = "Error: Connect to the database first!";
            return;
        }

        StatusText = $"Searching Discogs for {input}...";

        Album? album;
        try
        {
            // 1. Try Barcode First
            album = await _discogs.SearchAlbumByBarcodeAsync(input);

            // 2. Fallback to Catalog ID if Barcode fails
            if (album == null)
            {
                StatusText = $"Barcode not found. Trying Catalog ID for {input}...";
                album = await _discogs.SearchAlbumByCatalogIdAsync(input);
            }
        }
        catch (Exception ex)
        {
            StatusText = $"⚠️ Discogs Error: {ex.Message}";
            return;
        }

        // 3. Final failure check
        if (album == null)
        {
            StatusText = $"❌ '{input}' not found on Discogs.";
            return;
        }

        // 4. Save to SQLite (album and tracks together, so a failure can't leave half a CD behind)
        try
        {
            using (var conn = new SQLiteConnection(ConnectionString))
            {
                conn.Open();
                using var tx = conn.BeginTransaction();

                string sql = @"INSERT INTO albums (UPC, Artist, SortArtist, Title, Year, Format, Label, Genre, Styles)
                            VALUES (@upc, @artist, @sortArtist, @title, @year, @format, @label, @genre, @styles)";

                using (var cmd = new SQLiteCommand(sql, conn, tx))
                {
                    cmd.Parameters.AddWithValue("@upc", album.UPC);
                    cmd.Parameters.AddWithValue("@artist", album.Artist);
                    cmd.Parameters.AddWithValue("@sortArtist", album.SortArtist);
                    cmd.Parameters.AddWithValue("@title", album.Title);
                    cmd.Parameters.AddWithValue("@year", album.Year);
                    cmd.Parameters.AddWithValue("@format", album.Format);
                    cmd.Parameters.AddWithValue("@label", album.Label);
                    cmd.Parameters.AddWithValue("@genre", album.Genre);
                    cmd.Parameters.AddWithValue("@styles", album.Styles);
                    cmd.ExecuteNonQuery();
                }

                string trackSql = @"INSERT INTO tracks (UPC, Position, Track_Title, Duration)
                                    VALUES (@upc, @pos, @title, @dur)";

                foreach (var track in album.Tracks)
                {
                    using (var tCmd = new SQLiteCommand(trackSql, conn, tx))
                    {
                        tCmd.Parameters.AddWithValue("@upc", album.UPC);
                        tCmd.Parameters.AddWithValue("@pos", track.Position);
                        tCmd.Parameters.AddWithValue("@title", track.TrackTitle);
                        tCmd.Parameters.AddWithValue("@dur", track.Duration);
                        tCmd.ExecuteNonQuery();
                    }
                }
                tx.Commit();
            }

            AllAlbums.Add(album);
            AlbumCountText = $"{AllAlbums.Count} Albums";
            SelectedAlbum = album; // the view scrolls to it, ready for any editing
            StatusText = $"✅ Saved: {album.Artist} - {album.Title}";
        }
        catch (SQLiteException ex) when (ex.Message.Contains("UNIQUE constraint failed"))
        {
            SelectedAlbum = AllAlbums.FirstOrDefault(a => a.UPC == album.UPC);
            StatusText = $"⚠️ Duplicate! '{input}' is already in the database.";
        }
        catch (Exception ex)
        {
            StatusText = $"⚠️ Database Error: {ex.Message}";
        }
    }
}

// =====================
// Data Models
// =====================
public class Track
{
    public string TrackTitle { get; set; } = "";
    public string Duration { get; set; } = "";
    public string Position { get; set; } = "";
}

public class Album
{
    public string UPC { get; set; } = "";
    public string Artist { get; set; } = "";
    public string SortArtist { get; set; } = "";
    public string Title { get; set; } = "";
    public string Year { get; set; } = "";
    public string Format { get; set; } = "";
    public string Label { get; set; } = "";
    public string Genre { get; set; } = "";
    public string Styles { get; set; } = "";
    public ObservableCollection<Track> Tracks { get; set;} = new ();
}

public class WishListItem
{
    public int WishID { get; set; }
    public string Artist { get; set; } = "";
    public string Title { get; set; } = "";
    public string Format { get; set; } = "";
    public string Notes { get; set; } = "";
    public string DateAdded { get; set; } = "";
}
