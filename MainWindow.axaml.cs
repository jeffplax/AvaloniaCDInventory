using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading; 
using System.Runtime.InteropServices; 

namespace AvaloniaCDInventory
{
    public partial class MainWindow : Window
    {
        // --- Configuration & State ---
        private const string DB_FILE = "inventory.db";
        private string _connectionString = $"Data Source={DB_FILE};Version=3;";
        private string _currentDBPath = ""; 
        private ObservableCollection<Album> _allAlbums = new ObservableCollection<Album>();

        private string _wishlistDbConnection = ""; 
        private ObservableCollection<WishListItem> _wishlistItems = new ObservableCollection<WishListItem>(); 

        // --- Python & Watcher Variables ---
        private Process? _pythonProcess;
        private StreamWriter? _pythonInput;
        private FileSystemWatcher? _fileWatcher;

        public MainWindow()
        {
            InitializeComponent();
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version;

            var lblVersion = this.FindControl<TextBlock>("LblVersion");
            var lblBuildDate = this.FindControl<TextBlock>("LblBuildDate");
            if (lblVersion != null && version != null)
                {
                    lblVersion.Text = $"Version: {version.Major}.{version.Minor}.{version.Build}";
                }

            var buildDateAttr = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
                                .Cast<System.Reflection.AssemblyMetadataAttribute>()
                                .FirstOrDefault(attr => attr.Key == "BuildDate");
            
            if (lblBuildDate != null && buildDateAttr != null)
                {
                    lblBuildDate.Text = $"Build Date: {buildDateAttr.Value}";
                }

            GridAlbums.ItemsSource = _allAlbums;
            GridWishList.ItemsSource = _wishlistItems; 

            // Ensure Python shuts down if you close the window
            this.Closing += (s, e) => {
                if (_pythonProcess != null && !_pythonProcess.HasExited)
                    _pythonProcess.Kill();
            };
        }

        // --- DATABASE LOGIC ---
        private async void BtnConnectDB_Click(object? sender, RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select your Inventory Database",
                AllowMultiple = false,
                FileTypeFilter = new[] { 
                    new FilePickerFileType("SQLite Database") { Patterns = new[] { "*.db" } },
                    new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
                },
                SuggestedStartLocation = await topLevel.StorageProvider.TryGetFolderFromPathAsync(
                    Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..")))
            });

            if (files.Count >= 1)
            {
                string selectedPath = files[0].Path.LocalPath;
                _currentDBPath = selectedPath;
                _connectionString = $"Data Source={selectedPath};Version=3;";
                
                string dbFolder = Path.GetDirectoryName(selectedPath) ?? "";
                string wishlistPath = Path.Combine(dbFolder, "wishlist.db");
                _wishlistDbConnection = $"Data Source={wishlistPath};Version=3;";

                LoadWishlistFromDB(); 
                
                LoadDataFromDB();
                SetupFileWatcher(selectedPath);

                BtnRefresh.IsEnabled = true;
                BtnScanner.IsEnabled = true;
                LblStatus.Text = $"Connected: {Path.GetFileName(selectedPath)}";
                
                AppendConsole($"DB Connected: {selectedPath}");
            }
        }

        private string GetCurrentFilter()
        {
            if (CmbFilter.SelectedItem is ComboBoxItem cbi)
                return cbi.Content?.ToString() ?? "All";
            return "All";
        }

        private void LoadDataFromDB()
        {
            if (TxtSearch == null || CmbFilter == null || LblCount == null) return;
            try
            {
                _allAlbums.Clear(); 
                string filterCol = GetCurrentFilter();
                string searchQ = TxtSearch.Text ?? "";

                using (var conn = new SQLiteConnection(_connectionString))
                {
                    conn.Open();

                    var allTracks = new Dictionary<string, List<Track>>();
                    using (var trackCmd = new SQLiteCommand("SELECT UPC, Position, Track_Title, Duration FROM tracks", conn))
                    using (var trackReader = trackCmd.ExecuteReader())
                    {
                        while (trackReader.Read())
                        {
                            string upc = trackReader["UPC"]?.ToString() ?? "";
                            if (!allTracks.ContainsKey(upc))
                                allTracks[upc] = new List<Track>();
                                
                            allTracks[upc].Add(new Track {
                                Position = trackReader["Position"]?.ToString() ?? "",
                                TrackTitle = trackReader["Track_Title"]?.ToString() ?? "",
                                Duration = trackReader["Duration"]?.ToString() ?? ""
                            });
                        }
                    }

                    string sql = "SELECT * FROM albums";
                    if (!string.IsNullOrEmpty(searchQ))
                    {
                        if (filterCol == "All")
                            sql += " WHERE Artist LIKE @q OR Title LIKE @q OR Genre LIKE @q";
                        else
                            sql += $" WHERE {filterCol} LIKE @q";
                    }

                    using (var cmd = new SQLiteCommand(sql, conn))
                    {
                        if (!string.IsNullOrEmpty(searchQ))
                            cmd.Parameters.AddWithValue("@q", $"%{searchQ}%");

                        using (var r = cmd.ExecuteReader())
                        {
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
                                {
                                    newAlbum.Tracks = new ObservableCollection<Track>(allTracks[currentUpc]);
                                }

                                _allAlbums.Add(newAlbum);
                            }
                        }
                    }
                }
                LblCount.Text = $"{_allAlbums.Count} Albums";
            }
            catch (Exception ex)
            {
                AppendConsole($"\nDatabase Error: {ex.Message}");
            }
        }

        // --- UI EVENTS (SEARCH & FILTER) ---
        private void BtnRefresh_Click(object? sender, RoutedEventArgs e) => LoadDataFromDB();

        private void BtnCloseTracklist_Click(object? sender, RoutedEventArgs e)
        {
            if (GridAlbums != null)
            {
                GridAlbums.SelectedItem = null;
            }
        }

        private void ToggleCol_Click(object? sender, RoutedEventArgs e)
        {
            if (GridAlbums == null) return;
            
            if (sender is CheckBox chk && chk.Tag is string tagStr && int.TryParse(tagStr, out int colIndex))
            {
                if (colIndex >= 0 && colIndex < GridAlbums.Columns.Count)
                {
                    GridAlbums.Columns[colIndex].IsVisible = chk.IsChecked ?? false;
                }
            }
        }
        
        private void CmbFilter_SelectionChanged(object? sender, SelectionChangedEventArgs e) => LoadDataFromDB();
        
        private void TxtSearch_TextChanged(object? sender, TextChangedEventArgs e) => LoadDataFromDB();

        // --- PYTHON SCANNER LOGIC ---
        private void BtnStartScanner_Click(object? sender, RoutedEventArgs e)
        {
            string scriptName = "New_Discogs_v2.9.py";
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string projectRoot = Path.GetFullPath(Path.Combine(baseDir, @"..\..\.."));

            string pythonPath;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                pythonPath = Path.Combine(projectRoot, "venv", "Scripts", "python.exe");
            }
            else
            {
                pythonPath = Path.Combine(projectRoot, "venv", "bin", "python");
            }

            string scriptPath = Path.Combine(projectRoot, scriptName);

            if (!File.Exists(pythonPath))
            {
                 AppendConsole($"ERR: Could not find Python at: {pythonPath}");
                 return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = pythonPath,
                Arguments = $"-u \"{scriptPath}\" \"{_currentDBPath}\"", 
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(scriptPath)
            };

            try 
            {
                _pythonProcess = new Process { StartInfo = psi };
                _pythonProcess.OutputDataReceived += (s, args) => AppendConsole(args.Data);
                _pythonProcess.ErrorDataReceived += (s, args) => AppendConsole("ERR: " + args.Data);

                _pythonProcess.Start();
                _pythonProcess.BeginOutputReadLine();
                _pythonProcess.BeginErrorReadLine();
                _pythonInput = _pythonProcess.StandardInput;

                BtnScanner.IsEnabled = false;
                BtnScanner.Content = "Scanner Running";
                TxtScannerInput.Focus();
            }
            catch (Exception ex)
            {
                AppendConsole($"ERR: Could not start Python: {ex.Message}");
            }
        }

        private void TxtScannerInput_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && _pythonInput != null)
            {
                string cmd = TxtScannerInput.Text ?? "";
                AppendConsole($"> {cmd}");
                _pythonInput.WriteLine(cmd);
                _pythonInput.Flush();
                
                Dispatcher.UIThread.Post(() => TxtScannerInput.Text = "");
                e.Handled = true;
            }
        }

        private void AppendConsole(string? text)
        {
            if (string.IsNullOrEmpty(text)) return;
            
            Dispatcher.UIThread.Post(() => {
                TxtConsole.Text += text + Environment.NewLine;
                TxtConsole.CaretIndex = TxtConsole.Text.Length; 
            });
        }

        private void SetupFileWatcher(string filePath)
        {
            if (_fileWatcher != null) return;
            
            string folder = Path.GetDirectoryName(filePath)!;
            string file = Path.GetFileName(filePath);

            _fileWatcher = new FileSystemWatcher(folder, file)
            {
                NotifyFilter = NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };
            
            _fileWatcher.Changed += async (s, e) => {
                await Task.Delay(1000); 
                Dispatcher.UIThread.Post(() => LoadDataFromDB());
            };
        }
    
        private void GridAlbums_CellEditEnded(object? sender, DataGridCellEditEndedEventArgs e)
        {
            if (e.EditAction != DataGridEditAction.Commit) return;

            if (e.Row.DataContext is Album album)
            {
                try
                {
                    using (var conn = new SQLiteConnection(_connectionString))
                    {
                        conn.Open();
                        string sql = @"UPDATE albums 
                                    SET Artist = @artist, SortArtist = @sortArtist, Title = @title, Year = @year, 
                                        Format = @format, Label = @label,
                                        Genre = @genre, Styles = @styles
                                    WHERE UPC = @upc";

                        using (var cmd = new SQLiteCommand(sql, conn))
                        {
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
                        }
                    }
                    AppendConsole($"Saved: {album.Title}");
                }
                catch (Exception ex)
                {
                    AppendConsole($"ERR: Failed to save edit: {ex.Message}");
                    LoadDataFromDB(); 
                }
            }
        }

        private void GridAlbums_SelectionChanged(object? sender, SelectionChangedEventArgs e) { }

        private void GridAlbums_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete && GridAlbums.SelectedItem is Album selectedAlbum)
            {
                try 
                {
                    using (var conn = new SQLiteConnection(_connectionString))
                    {
                        conn.Open();
                        using (var cmd = new SQLiteCommand("DELETE FROM albums WHERE UPC = @upc", conn))
                        {
                            cmd.Parameters.AddWithValue("@upc", selectedAlbum.UPC);
                            cmd.ExecuteNonQuery();
                        }
                    }
                    
                    _allAlbums.Remove(selectedAlbum);
                    LblCount.Text = $"{_allAlbums.Count} Albums";
                    AppendConsole($"Deleted: {selectedAlbum.Title}");
                }
                catch (Exception ex)
                {
                    AppendConsole($"ERR: Could not delete: {ex.Message}");
                }
            }
        }

        private void GridWishList_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete && GridWishList.SelectedItem is WishListItem selectedItem)
            {
                try
                {
                    using (var conn = new SQLiteConnection(_wishlistDbConnection))
                    {
                        conn.Open();
                        using (var cmd = new SQLiteCommand("DELETE FROM wishlist WHERE WishID = @id", conn))
                        {
                            cmd.Parameters.AddWithValue("@id", selectedItem.WishID);
                            cmd.ExecuteNonQuery();
                        }
                    }
                    _wishlistItems.Remove(selectedItem);
                    AppendConsole($"Deleted target: {selectedItem.Title}");
                }
                catch (Exception ex)
                {
                    AppendConsole($"ERR deleting wishlist item: {ex.Message}");
                }
            }
        }       

        private void LoadWishlistFromDB()
        {
            if (string.IsNullOrEmpty(_wishlistDbConnection)) return;

            try
            {
                _wishlistItems.Clear();

                using (var conn = new SQLiteConnection(_wishlistDbConnection))
                {
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

                    using (var cmd = new SQLiteCommand("SELECT * FROM wishlist ORDER BY DateAdded DESC", conn))
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            _wishlistItems.Add(new WishListItem
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
                }
            }
            catch (Exception ex)
            {
                AppendConsole($"ERR loading wishlist: {ex.Message}");
            }
        }

        private void BtnSaveWish_Click(object? sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_wishlistDbConnection))
                {
                    AppendConsole("Wishlist Error: You must click 'Connect DB' on the Master Library tab first.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(TxtWishTitle.Text))
                {
                    AppendConsole("Wishlist Error: The 'Title' box cannot be empty.");
                    return;
                }
            try
            {
                using (var conn = new SQLiteConnection(_wishlistDbConnection))
                {
                    conn.Open();
                    string sql = @"INSERT INTO wishlist (Artist, Title, Format, Notes, DateAdded) 
                                   VALUES (@artist, @title, @format, @notes, @dateAdded)";
                    
                    using (var cmd = new SQLiteCommand(sql, conn))
                    {
                        cmd.Parameters.AddWithValue("@artist", TxtWishArtist.Text ?? "");
                        cmd.Parameters.AddWithValue("@title", TxtWishTitle.Text);
                        
                        string formatVal = (CmbWishFormat.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
                        cmd.Parameters.AddWithValue("@format", formatVal);
                        
                        cmd.Parameters.AddWithValue("@notes", TxtWishNotes.Text ?? "");
                        cmd.Parameters.AddWithValue("@dateAdded", DateTime.Now.ToString("yyyy-MM-dd"));
                        
                        cmd.ExecuteNonQuery();
                    }
                }

                TxtWishArtist.Text = "";
                TxtWishTitle.Text = "";
                TxtWishNotes.Text = "";
                CmbWishFormat.SelectedIndex = 0;

                AppendConsole("Target added to Wish List.");
                LoadWishlistFromDB(); 
            }
            catch (Exception ex)
            {
                AppendConsole($"ERR saving to wishlist: {ex.Message}");
            }
        }

    } 

    // ====================================================================
    // DATA MODELS
    // ====================================================================
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
        public ObservableCollection<Track> Tracks { get; set; } = new ObservableCollection<Track>();        
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
}