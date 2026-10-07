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
        private const int MinBarcodeLength = 8; // EAN-8 is the shortest retail barcode; shorter digit runs are typing
        private StringBuilder _barcodeBuffer = new StringBuilder();
        private DateTime _lastKeystroke = DateTime.Now;
        private bool _swallowNextEnterKeyUp;

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

                BtnRefresh.IsEnabled = true;
            }
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
            if (DataContext is MainWindowViewModel vm)
            {
                vm.LoadMasterLibrary();
                vm.LoadWishlist();
            }
        }

        private void MainTabControl_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            // The phone may have synced new targets into wishlist.db (via OneDrive) since we last looked
            if (e.Source == MainTabControl && MainTabControl.SelectedItem == TabWishList && DataContext is MainWindowViewModel vm)
                vm.LoadWishlist();
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

        private void GridAlbums_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            // Keep the selected CD on screen (e.g. a newly scanned CD, or the one being edited after a refresh)
            if (GridAlbums.SelectedItem is Album album)
                Dispatcher.UIThread.Post(() => GridAlbums.ScrollIntoView(album, null), DispatcherPriority.Background);
        }

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

            // Catch both Enter and Return depending on how the OS maps the scanner's termination key.
            // Short digit runs (e.g. typing a Year into a cell) are left alone so Enter still commits the edit.
            if ((e.Key == Key.Enter || e.Key == Key.Return) && _barcodeBuffer.Length >= MinBarcodeLength)
            {
                string scannedCode = _barcodeBuffer.ToString();
                _barcodeBuffer.Clear();

                // Stop the Enter from reaching other controls. This must happen before the await: the event
                // finishes routing as soon as we yield, and an unhandled Enter makes the DataGrid select its
                // first row and open that CD's tracklist.
                e.Handled = true;
                _swallowNextEnterKeyUp = true;

                if (DataContext is MainWindowViewModel vm)
                {
                    await vm.ProcessBarcodeAsync(scannedCode);
                }
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
            // Swallow the KeyUp that matches a scanner's Enter, so it can't act on the grid either
            if ((e.Key == Key.Enter || e.Key == Key.Return) && _swallowNextEnterKeyUp)
            {
                _swallowNextEnterKeyUp = false;
                e.Handled = true;
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