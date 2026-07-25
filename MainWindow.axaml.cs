using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Text.Json;

namespace AvaloniaCDInventory
{
    public partial class MainWindow : Window
    {
        // Native barcode scanning variables
        private StringBuilder _barcodeBuffer = new StringBuilder();
        private DateTime _lastKeystroke = DateTime.Now;

        // File watcher for hot-reloading the database
        private FileSystemWatcher? _fileWatcher;

        public MainWindow()
        {
            InitializeComponent();

            // Build the version and build date labels
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
            
            this.AddHandler(InputElement.KeyDownEvent, Window_PreviewKeyDown, RoutingStrategies.Tunnel);
            this.AddHandler(InputElement.KeyUpEvent, Window_PreviewKeyUp, RoutingStrategies.Tunnel);
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
                string dbFolder = Path.GetDirectoryName(selectedPath) ?? "";
                string wishlistPath = Path.Combine(dbFolder, "wishlist.db");
                
                if (DataContext is MainWindowViewModel vm)
                {
                    // Pass the verified file paths directly into the ViewModel
                    vm.InitializeConnections(selectedPath, wishlistPath);
                    vm.StatusText = $"Connected: {Path.GetFileName(selectedPath)}";
                }

                // Load main data and wishlist
                SetupFileWatcher(selectedPath);
                BtnRefresh.IsEnabled = true;
            }
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

            _fileWatcher.Changed += async (s, e) =>
            {
                await Task.Delay(1000);
                Dispatcher.UIThread.Post(() =>
                {
                    if (DataContext is MainWindowViewModel vm) vm.LoadMasterLibrary();
                });
            };
        }

        // --- Pure UI handlers ---
        private void BtnCloseTracklist_Click(object? sender, RoutedEventArgs e)
        {
            if (GridAlbums != null) GridAlbums.SelectedItem = null;
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

        private void BtnRefresh_Click(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm) vm.LoadMasterLibrary();
        }
        
        private void CmbFilter_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm && CmbFilter.SelectedItem is ComboBoxItem cbi)
            {
                vm.SelectedFilter = cbi.Content?.ToString() ?? "All";
            }
        }
        
        private void TxtSearch_TextChanged(object? sender, TextChangedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm) vm.SearchQuery = TxtSearch.Text ?? "";
        }
    
        private void BtnSaveWish_Click(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                // Assign the UI text box values to ViewModel properties
                vm.WishArtist = TxtWishArtist.Text ?? "";
                vm.WishTitle = TxtWishTitle.Text ?? "";
                vm.WishFormat = (CmbWishFormat.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "CD";
                vm.WishNotes = TxtWishNotes.Text ?? "";

                // execute the ViewModel command
                vm.SaveWishCommand.Execute(null);

                // Clear the UI text boxes
                TxtWishArtist.Text = "";
                TxtWishTitle.Text = "";
                CmbWishFormat.SelectedIndex = 0;
                TxtWishNotes.Text = "";
            }
        }

        private async void BtnExportLibrary_Click(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm || !vm.AllAlbums.Any())
            {
                if (DataContext is MainWindowViewModel v) v.StatusText = "No albums to export.";
                return;
            }

            // Proceed with export logic here
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export Master Library",
                SuggestedFileName = "MasterLibrary.csv",
                DefaultExtension = "csv",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("CSV Document") { Patterns = new[] { "*.csv" } },
                    new FilePickerFileType("JSON Document") { Patterns = new[] { "*.json" } }
                }
            });

            if (file != null)
            {
                try
                {
                    await using var stream = await file.OpenWriteAsync();
                    using var writer = new StreamWriter(stream, Encoding.UTF8);

                    if (file.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        var options = new JsonSerializerOptions { WriteIndented = true };
                        string json = JsonSerializer.Serialize(vm.AllAlbums, options);
                        await writer.WriteAsync(json);
                    }
                    else
                    {
                        // CSV Export
                        await writer.WriteLineAsync("UPC,Artist,SortArtist,Title,Year,Format,Label,Genre,Styles");
                        foreach (var album in vm.AllAlbums)
                        {
                            await writer.WriteLineAsync($"\"{EscapeCsv(album.UPC)}\",\"{EscapeCsv(album.Artist)}\",\"{EscapeCsv(album.SortArtist)}\",\"{EscapeCsv(album.Title)}\",\"{EscapeCsv(album.Year)}\",\"{EscapeCsv(album.Format)}\",\"{EscapeCsv(album.Label)}\",\"{EscapeCsv(album.Genre)}\",\"{EscapeCsv(album.Styles)}\"");
                        }
                    }
                    vm.StatusText = $"✅ Exported successfully to {file.Name}";
                }
                catch (Exception ex)
                {
                    vm.StatusText = $"⚠️ Export Error: {ex.Message}";
                }
            }
        }

        private async void BtnExportWishlist_Click(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm || !vm.WishlistItems.Any())
            {
                if (DataContext is MainWindowViewModel v) v.StatusText = "No wishlist data to export.";
                return;
            }

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export Wishlist",
                SuggestedFileName = "WishlistExport.csv",
                DefaultExtension = "csv",
                FileTypeChoices = new[] {
                    new FilePickerFileType("CSV Document") { Patterns = new[] { "*.csv" } }
                }
            });

            if (file != null)
            {
                try
                {
                    await using var stream = await file.OpenWriteAsync();
                    using var writer = new StreamWriter(stream, Encoding.UTF8);

                    await writer.WriteLineAsync("Artist,Title,Format,Notes,DateAdded");
                    foreach (var item in vm.WishlistItems)
                    {
                        await writer.WriteLineAsync($"\"{EscapeCsv(item.Artist)}\",\"{EscapeCsv(item.Title)}\",\"{EscapeCsv(item.Format)}\",\"{EscapeCsv(item.Notes)}\",\"{EscapeCsv(item.DateAdded)}\"");
                    }
                    vm.StatusText = $"✅ Wishlist exported to {file.Name}";
                }
                catch (Exception ex)
                {
                    vm.StatusText = $"⚠️ Export Error: {ex.Message}";
                }
            }
        }

        private string EscapeCsv(string input)
        {
            if (string.IsNullOrEmpty(input)) return "";
            return input.Replace("\"", "\"\""); // Escape quotes to prevent CSV column breaking
        }

        private void GridAlbums_CellEditEnded(object? sender, DataGridCellEditEndedEventArgs e)
        {
            if (e.EditAction == DataGridEditAction.Commit && e.Row.DataContext is Album album)
            {
                if (DataContext is MainWindowViewModel vm) vm.UpdateAlbum(album);
            }

        }

        private void GridAlbums_SelectionChanged(object? sender, SelectionChangedEventArgs e) { }

        private void GridAlbums_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete && GridAlbums.SelectedItem is Album selectedAlbum)
            {
                if (DataContext is MainWindowViewModel vm) vm.DeleteAlbum(selectedAlbum);
            }
        }

        private void GridWishList_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete && GridWishList.SelectedItem is WishListItem selectedItem)
            {
                if (DataContext is MainWindowViewModel vm) vm.DeleteWishlistItem(selectedItem);
            }
        }       

        private async void Window_PreviewKeyDown(object? sender, KeyEventArgs e)
        {
            // Increased buffer to 250ms to accommodate varying USB scanner speeds
            if ((DateTime.Now - _lastKeystroke).TotalMilliseconds > 250)
            {
                _barcodeBuffer.Clear();
            }
            
            _lastKeystroke = DateTime.Now;

            // Catch both Enter and Return depending on how the OS maps the scanner's termination key
            if ((e.Key == Key.Enter || e.Key == Key.Return) && _barcodeBuffer.Length > 0)
            {
                string scannedCode = _barcodeBuffer.ToString();
                _barcodeBuffer.Clear();

                if (DataContext is MainWindowViewModel vm)
                {
                    await vm.ProcessBarcodeAsync(scannedCode);
                }

                // Stop the event from reaching other controls
                e.Handled = true;
                return;
            }

            // Map the top-row number and numpad keys into the buffer
            if (e.Key >= Key.D0 && e.Key <= Key.D9)
            {
                _barcodeBuffer.Append((char)('0' + (e.Key - Key.D0)));
            }
            else if (e.Key >= Key.NumPad0 && e.Key <= Key.NumPad9)
            {
                _barcodeBuffer.Append((char)('0' + (e.Key - Key.NumPad0)));
            }
        }

        private void Window_PreviewKeyUp(object? sender, KeyEventArgs e)
        {
            // If the scanner just fired an Enter key, swallow the KeyUp event 
            // so the DataGrid doesn't expand the selected row.
            if (e.Key == Key.Enter || e.Key == Key.Return)
            {
                // If it has been less than 500ms since our last barcode buffer reset, 
                // this was a scanner input, not a human pressing Enter.
                if ((DateTime.Now - _lastKeystroke).TotalMilliseconds < 500)
                {
                    e.Handled = true;
                }
            }
        }

        private void BtnCloseAbout_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            // Close the 'About' box
            var tabControl = this.FindControl<Avalonia.Controls.TabControl>("MainTabControl");
            if (tabControl != null)
            {
                tabControl.SelectedIndex = 0; // Switch back to the main library tab
            }
        }
    } 
}