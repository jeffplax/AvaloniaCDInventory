# 💿 CD Inventory Manager (Desktop and Mobile Android version)

A cross-platform, local-first inventory and wishlist management system designed specifically for large classical music collections. Built with .NET, Avalonia UI, and SQLite, this project features a dual-application architecture (Windows Desktop & Android Mobile) synchronized via a sidecar database model over OneDrive.

## 🏗️ Architecture

The system relies on a local SQLite database architecture, intentionally bypassing cloud-hosted APIs in favor of user-owned, heavily customizable local files.

* **`inventory.db`**: The master library containing the full schema of albums, tracklists, and metadata.
* **`wishlist.db`**: A lightweight "sidecar" database handling targets and record-store hunting lists.

Because Android strictly sandboxes application data, the phone keeps its own copies of both files for offline access when browsing physical record stores. `inventory.db` is copied down to the phone (read-only there). `wishlist.db` is **merged** both ways: the phone's *Sync with OneDrive* button picks the shared file and merges it row by row (newest change to each Artist + Title wins, and deletes carry over), so targets added or removed on either device are kept.

## ✨ Features

### Desktop Application (Windows)
* **Master Library Dashboard:** Filter, sort, and search your entire collection instantly.
* **Dynamic DataGrid:** Toggleable columns, inline editing, and row-details views for complete tracklists.
* **Wishlist Management:** Direct database manipulation for adding and removing targets.
* **Python Scanner Integration:** ~~Built-in console and process runner to execute `New_Discogs_v2.9.py`, injecting new CD metadata and Semantic Knowledge Graph data directly into the database.~~ (removed in V2.0. This feature is now integrated within the C# code.)

### Mobile Application (Android)
* **Optimized UI:** Touch-friendly layout built specifically for modern Android devices.
* **Secure Vault Sync:** Integrates with Android's secure `StorageProvider` to pull/push SQLite databases to and from OneDrive.
* **Standalone Wishlist:** Add discoveries while at the record store, saving them to the local Android vault to be pushed back to the master library later.
* **Automated Build Metadata:** Custom MSBuild targets dynamically calculate and inject the exact compilation timestamp into the app's About page.

## 🛠️ Tech Stack
* **Language:** C# 12, XAML, Python 3
* **Framework:** .NET 10
* **UI:** Avalonia UI (Desktop) / .NET Android (Mobile)
* **Database:** SQLite (`Microsoft.Data.Sqlite` & `System.Data.SQLite`)
* **Metadata/Scanner:** ~~Python (via `subprocess` execution)~~ C#

## 🚀 Getting Started

### Prerequisites
* [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
* ~~Python 3.x (for the Discogs scanner)~~
* A [Discogs personal access token](https://www.discogs.com/settings/developers), saved in `%APPDATA%\AvaloniaCDInventory\discogs_token.txt` (or set the `DISCOGS_TOKEN` environment variable)
* Android SDK (if compiling the mobile application)

### Running the Desktop App
1. Clone the repository and navigate to the desktop project:
   ```powershell
   git clone [https://github.com/yourusername/AvaloniaCDInventory.git](https://github.com/yourusername/AvaloniaCDInventory.git)
   cd AvaloniaCDInventory
