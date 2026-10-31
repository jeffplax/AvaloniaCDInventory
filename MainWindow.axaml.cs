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
        // Native barcode scanning
        // A USB scanner "types" the barcode digits very quickly and ends with Enter. Typed digits are held back
        // instead of going straight to the focused control; if Enter arrives while enough digits are held, it was
        // a scan. If the digits stop without an Enter (i.e. a person typing), they are released to the control.
        private const int MinBarcodeLength = 8;  // EAN-8 is the shortest retail barcode; shorter digit runs are typing
        private const int ScanGapMs = 250;       // max gap between a scanner's keystrokes (tuned for slower USB scanners)
        private readonly StringBuilder _heldDigits = new StringBuilder();
        private readonly DispatcherTimer _releaseTimer;
        private bool _replayingDigits;
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
            
            _releaseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ScanGapMs) };
            _releaseTimer.Tick += (_, _) => ReleaseHeldDigits();

            this.AddHandler(InputElement.KeyDownEvent, Window_PreviewKeyDown, RoutingStrategies.Tunnel);
            this.AddHandler(InputElement.KeyUpEvent, Window_PreviewKeyUp, RoutingStrategies.Tunnel);
            this.AddHandler(InputElement.TextInputEvent, Window_PreviewTextInput, RoutingStrategies.Tunnel);
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

        private async void GridAlbums_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete && GridAlbums.SelectedItem is Album selectedAlbum && DataContext is MainWindowViewModel vm)
            {
                // Mark handled before awaiting: the event finishes routing as soon as we yield
                e.Handled = true;

                // Deleting removes the CD and its tracklist for good, so ask first
                string name = string.IsNullOrWhiteSpace(selectedAlbum.Artist)
                    ? selectedAlbum.Title
                    : $"{selectedAlbum.Artist} - {selectedAlbum.Title}";
                bool confirmed = await ConfirmDialog.ShowAsync(this, "Delete CD",
                    $"Delete \"{name}\" and its tracklist from the library?\n\nThis can't be undone.");

                if (confirmed) vm.DeleteAlbum(selectedAlbum);
                GridAlbums.Focus();
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
            // Catch both Enter and Return depending on how the OS maps the scanner's termination key
            if (e.Key == Key.Enter || e.Key == Key.Return)
            {
                if (_heldDigits.Length >= MinBarcodeLength)
                {
                    _releaseTimer.Stop();
                    string scannedCode = _heldDigits.ToString();
                    _heldDigits.Clear();

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

                // A short digit run (e.g. a Year typed into a cell): hand the digits over first,
                // then let Enter commit the edit as usual
                ReleaseHeldDigits();
                return;
            }

            // Any other key (Backspace, arrows, letters...) must act after the digits typed before it,
            // so release them now. Digit keys themselves are collected in Window_PreviewTextInput.
            if (!IsDigitKey(e.Key) && e.Key != Key.LeftShift && e.Key != Key.RightShift)
            {
                ReleaseHeldDigits();
            }
        }

        private void Window_PreviewTextInput(object? sender, TextInputEventArgs e)
        {
            if (_replayingDigits || string.IsNullOrEmpty(e.Text)) return;

            if (e.Text.All(char.IsAsciiDigit))
            {
                // Hold the digits back until we know whether this is a scan or typing
                _heldDigits.Append(e.Text);
                e.Handled = true;
                _releaseTimer.Stop();
                _releaseTimer.Start();
                return;
            }

            // Non-digit text: let any held digits through first so the typed order is kept
            ReleaseHeldDigits();
        }

        // Gives held digits back to whichever control has focus, exactly as if they had just been typed
        private void ReleaseHeldDigits()
        {
            _releaseTimer.Stop();
            if (_heldDigits.Length == 0) return;

            string digits = _heldDigits.ToString();
            _heldDigits.Clear();

            if (FocusManager?.GetFocusedElement() is not Interactive target) return;

            _replayingDigits = true;
            try
            {
                target.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = digits });
            }
            finally
            {
                _replayingDigits = false;
            }
        }

        private static bool IsDigitKey(Key key) =>
            (key >= Key.D0 && key <= Key.D9) || (key >= Key.NumPad0 && key <= Key.NumPad9);

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