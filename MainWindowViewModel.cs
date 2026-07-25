using System;
using System.Collections.ObjectModel;
using System.Data.SQLite;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AvaloniaCDInventory;

public partial class MainWindowViewModel : ObservableObject
{
private readonly DiscogsService _discogs = new();
    
    [ObservableProperty]
    private string _connectionString = string.Empty;
    
    [ObservableProperty]
    private string _wishlistConnectionString = string.Empty;

    public ObservableCollection<Album> AllAlbums { get; } = new();
    public ObservableCollection<WishListItem> WishlistItems { get; } = new();

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
        
        try
        {
            AllAlbums.Clear(); 
            using var conn = new SQLiteConnection(ConnectionString);
            conn.Open();

            var allTracks = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<Track>>();
            using (var trackCmd = new SQLiteCommand("SELECT UPC, Position, Track_Title, Duration FROM tracks", conn))
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
                if (SelectedFilter == "All")
                    sql += " WHERE Artist LIKE @q OR Title LIKE @q OR Genre LIKE @q";
                else
                    sql += $" WHERE {SelectedFilter} LIKE @q";
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
        }
        catch (Exception ex)
        {
            StatusText = $"Database Error: {ex.Message}";
        }
    }

    [RelayCommand]
    public void LoadWishlist()
    {
        if (string.IsNullOrEmpty(WishlistConnectionString)) return;

        try
        {
            WishlistItems.Clear();
            using var conn = new SQLiteConnection(WishlistConnectionString);
            conn.Open();
            
            string createSql = @"CREATE TABLE IF NOT EXISTS wishlist (
                                    WishID INTEGER PRIMARY KEY AUTOINCREMENT,
                                    Artist TEXT,
                                    Title TEXT NOT NULL,
                                    Format TEXT,
                                    Notes TEXT,
                                    DateAdded TEXT
                                )";
            using (var createCmd = new SQLiteCommand(createSql, conn)) { createCmd.ExecuteNonQuery(); }

            using var cmd = new SQLiteCommand("SELECT * FROM wishlist ORDER BY DateAdded DESC", conn);
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
            string sql = @"INSERT INTO wishlist (Artist, Title, Format, Notes, DateAdded) 
                           VALUES (@artist, @title, @format, @notes, @dateAdded)";
            
            using var cmd = new SQLiteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@artist", WishArtist ?? "");
            cmd.Parameters.AddWithValue("@title", WishTitle);
            cmd.Parameters.AddWithValue("@format", WishFormat ?? "");
            cmd.Parameters.AddWithValue("@notes", WishNotes ?? "");
            cmd.Parameters.AddWithValue("@dateAdded", DateTime.Now.ToString("yyyy-MM-dd"));
            cmd.ExecuteNonQuery();

            WishArtist = ""; WishTitle = ""; WishNotes = "";
            StatusText = "Target added to Wish List.";
            LoadWishlist(); 
        }
        catch (Exception ex)
        {
            StatusText = $"Error saving to wishlist: {ex.Message}";
        }
    }

    [RelayCommand]
    public void DeleteAlbum(Album album)
    {
        if (album == null || string.IsNullOrEmpty(ConnectionString)) return;
        try 
        {
            using var conn = new SQLiteConnection(ConnectionString);
            conn.Open();
            using var cmd = new SQLiteCommand("DELETE FROM albums WHERE UPC = @upc", conn);
            cmd.Parameters.AddWithValue("@upc", album.UPC);
            cmd.ExecuteNonQuery();
            
            AllAlbums.Remove(album);
            AlbumCountText = $"{AllAlbums.Count} Albums";
            StatusText = $"Deleted: {album.Title}";
        }
        catch (Exception ex) { StatusText = $"Error deleting album: {ex.Message}"; }
    }

    [RelayCommand]
    public void DeleteWishlistItem(WishListItem item)
    {
        if (item == null || string.IsNullOrEmpty(WishlistConnectionString)) return;
        try
        {
            using var conn = new SQLiteConnection(WishlistConnectionString);
            conn.Open();
            using var cmd = new SQLiteCommand("DELETE FROM wishlist WHERE WishID = @id", conn);
            cmd.Parameters.AddWithValue("@id", item.WishID);
            cmd.ExecuteNonQuery();
            
            WishlistItems.Remove(item);
            StatusText = $"Deleted target: {item.Title}";
        }
        catch (Exception ex) { StatusText = $"Error deleting wishlist item: {ex.Message}"; }
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

        // 1. Try Barcode First
        var album = await _discogs.SearchAlbumByBarcodeAsync(input);
        
        // 2. Fallback to Catalog ID if Barcode fails
        if (album == null)
        {
            StatusText = $"Barcode not found. Trying Catalog ID for {input}...";
            album = await _discogs.SearchAlbumByCatalogIdAsync(input);
        }

        // 3. Final failure check
        if (album == null)
        {
            StatusText = $"❌ '{input}' not found on Discogs.";
            return;
        }

        // 4. Save to SQLite
        try
        {
            using (var conn = new SQLiteConnection(ConnectionString))
            {
                conn.Open();
                
                string sql = @"INSERT INTO albums (UPC, Artist, SortArtist, Title, Year, Format, Label, Genre, Styles)
                            VALUES (@upc, @artist, @sortArtist, @title, @year, @format, @label, @genre, @styles)";
                
                using (var cmd = new SQLiteCommand(sql, conn))
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

                // Notice: Track_Title with the underscore is fixed here!
                string trackSql = @"INSERT INTO tracks (UPC, Position, Track_Title, Duration)
                                    VALUES (@upc, @pos, @title, @dur)";
                
                foreach (var track in album.Tracks)
                {
                    using (var tCmd = new SQLiteCommand(trackSql, conn))
                    {
                        tCmd.Parameters.AddWithValue("@upc", album.UPC);
                        tCmd.Parameters.AddWithValue("@pos", track.Position);
                        tCmd.Parameters.AddWithValue("@title", track.TrackTitle);
                        tCmd.Parameters.AddWithValue("@dur", track.Duration);
                        tCmd.ExecuteNonQuery();
                    }
                }
            }

            AllAlbums.Add(album);
            AlbumCountText = $"{AllAlbums.Count} Albums";
            StatusText = $"✅ Saved: {album.Artist} - {album.Title}";
        }
        catch (SQLiteException ex) when (ex.Message.Contains("UNIQUE constraint failed"))
        {
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