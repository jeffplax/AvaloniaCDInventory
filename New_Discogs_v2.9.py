import discogs_client
import sqlite3
import time
import os
import sys
import io
import re

# Version 2.8 - SQLite Backend with Enhanced Duplicate Handling and Robustness
# Version 2.9 - Added auto-migration for new Genre and Styles columns and improved error handling
# Version 2.9.1 - fixed inventory.db argument path to allow for full paths to be passed in,
#                 and print the path to the console for verification

# Force UTF-8 output safely
if sys.stdout.encoding != 'utf-8':
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')

# --- CONFIGURATION ---
USER_TOKEN = 'RBLNSEvUdUaulinZxiwGnYXnAdCrMlUMNDyJLkvm'

# Default to 'inventory.db' if no argument is passed, otherwise use the provided path
DB_FILE = 'inventory.db'

# if argument is passed, use it as the DB file path
if len(sys.argv) > 1:
    DB_FILE = sys.argv[1]

# Print it so we can verify in the green console
print(f"   📂 Database: {DB_FILE}")    

ESTIMATED_TOTAL = 800
# ---------------------

def init_db():
    """Ensures DB exists and performs auto-migration for new columns."""
    conn = sqlite3.connect(DB_FILE)
    cursor = conn.cursor()
    
    # Enable foreign keys
    cursor.execute("PRAGMA foreign_keys = ON;")
    
    # Create Tables (If they don't exist)
    cursor.execute('''
        CREATE TABLE IF NOT EXISTS albums (
            UPC TEXT PRIMARY KEY,
            Artist TEXT,
            SortArtist TEXT,
            Title TEXT,
            Year TEXT,
            Format TEXT,
            Label TEXT,
            Genre TEXT,    
            Styles TEXT    
        )
    ''')

    cursor.execute('''
        CREATE TABLE IF NOT EXISTS tracks (
            TrackID INTEGER PRIMARY KEY AUTOINCREMENT,
            UPC TEXT,
            Position TEXT,
            Parent_Work TEXT,
            Track_Title TEXT,
            Duration TEXT,
            FOREIGN KEY(UPC) REFERENCES albums(UPC) ON DELETE CASCADE
        )
    ''')
    
    # AUTO-MIGRATION: Add columns to existing DB if missing
    # We check the table info to see if 'Genre' exists
    cursor.execute("PRAGMA table_info(albums)")
    columns = [info[1] for info in cursor.fetchall()]
    
    if 'SortArtist' not in columns:
        print("   🛠️  Migrating Database: Adding 'SortArtist' column...")
        cursor.execute("ALTER TABLE albums ADD COLUMN SortArtist TEXT")
        
    if 'Genre' not in columns:
        print("   🛠️  Migrating Database: Adding 'Genre' column...")
        cursor.execute("ALTER TABLE albums ADD COLUMN Genre TEXT")
        
    if 'Styles' not in columns:
        print("   🛠️  Migrating Database: Adding 'Styles' column...")
        cursor.execute("ALTER TABLE albums ADD COLUMN Styles TEXT")
    
    conn.commit()
    conn.close()

def get_db_count():
    try:
        conn = sqlite3.connect(DB_FILE)
        cursor = conn.cursor()
        cursor.execute("SELECT COUNT(*) FROM albums")
        count = cursor.fetchone()[0]
        conn.close()
        return count
    except:
        return 0

def generate_sort_artist(artist_name):
    # Cleans Discogs artifacts and formats the artist name for sorting
    # Remove any text in parentheses or brackets
    clean_name = re.sub(r'\s\(\d+\)$', '', artist_name).strip()
    
    # handle standard prefixes for sorting
    lower_name = clean_name.lower()
    if lower_name.startswith('the '):
        return clean_name[4:] + ', The'
    elif lower_name.startswith('a '):
        return clean_name[2:] + ', A'
    elif lower_name.startswith('an '):
        return clean_name[3:] + ', An'
    
    return clean_name
    

def main():
    # 0. Setup DB
    init_db()

    # 1. Initialize Client
    try:
        d = discogs_client.Client('MyCDScanner/TriMode/1.0', user_token=USER_TOKEN)
        me = d.identity() 
        print(f"\n--- DISCOGS LIBRARY INGESTOR (SQLite) ---")
        print(f"Logged in as: {me.username}")
    except Exception as e:
        print(f"Error logging in: {e}")
        return

    current_count = get_db_count()
    print(f"Resuming at item #{current_count + 1}")
    print(f"Commands: Scan UPC, 'c' (Catalog #), 'm' (Manual), or 'x' (Exit)")
    print(f"Data saves instantly to '{DB_FILE}'\n")

    try:
        while True:
            prompt_text = f"Input [{current_count + 1}/{ESTIMATED_TOTAL}] (or 'x' to exit): "
            print(prompt_text, end="", flush=True)    # Print the prompt explicitly
            user_input = sys.stdin.readline().strip() # Read raw input 
            
            if not user_input: continue

            if user_input.lower() in ['x', 'exit', 'quit']:
                print("\nSaving and exiting... Goodbye!")
                break

            release = None
            final_id = user_input 

            # BRANCH A: CATALOG SEARCH ('c')
            if user_input.lower() in ['c', 'cat']:
                print("   Enter Catalog Number: ", end="", flush=True)
                cat_num = sys.stdin.readline().strip()
                if not cat_num: continue
                print(f"   Searching Catalog #: {cat_num}...")
                try:
                    results = d.search(cat_num, type='release', catno=cat_num)
                    release, final_id = select_from_results(results, f"CAT_{cat_num}")
                except Exception as e:
                    print(f"   ⚠️ Search Error: {e}")
                    continue

            # BRANCH B: MANUAL TEXT SEARCH ('m')
            elif user_input.lower() in ['m', 'manual']:
                print("   Enter Search (Artist - Title): ", end="", flush=True)
                query = sys.stdin.readline().strip()
                if not query: continue
                print(f"   Searching Text: {query}...")
                try:
                    results = d.search(query, type='release')
                    release, final_id = select_from_results(results, f"MANUAL_{query}")
                except Exception as e:
                    print(f"   ⚠️ Search Error: {e}")
                    continue

            # BRANCH C: BARCODE SCAN (Default)
            else:
                if is_duplicate(user_input):
                    print(f"   ⚠️  Duplicate! UPC {user_input} is already in database.")
                    continue

                print(f"   Fetching UPC...", end=" ")
                try:
                    results = d.search(user_input, type='release', barcode=user_input)
                    try:
                        release = results[0]
                        final_id = user_input 
                    except (IndexError, StopIteration):
                        print(f"❌ Not found via Barcode.")
                        continue
                except Exception as e:
                    print(f"   ⚠️ API Error: {e}")
                    continue

            # SAVE DATA
            if release:
                # Ensure ID uniqueness for non-barcode inputs
                if user_input.lower() in ['c', 'm', 'cat', 'manual']:
                    final_id = f"DISCOGS_ID_{release.id}"
                
                if is_duplicate(final_id):
                     print(f"   ⚠️  Duplicate! You already have this release (ID: {release.id}).")
                     continue

                if save_to_db(release, final_id):
                    current_count += 1
                
                time.sleep(1.1)

    except KeyboardInterrupt:
        print(f"\n\nStopping at {current_count}/{ESTIMATED_TOTAL}.")

# --- HELPER FUNCTIONS ---

def select_from_results(results, backup_id_prefix):
    """Handles the UI for choosing 1 of 20 results."""
    try:
        if not results:
            print("   ❌ No results found.")
            return None, None
    except:
        pass

    print(f"   --- Top Matches ---")
    
    found_releases = []
    try:
        for i, r in enumerate(results):
            if i >= 20: break
            
            fmt = "Unknown"
            if 'formats' in r.data and r.data['formats']:
                fmt = r.data['formats'][0].get('name', 'Unknown')
            
            yr = r.data.get('year', '????')
            
            lbl = "Unknown Label"
            cat = ""
            if 'labels' in r.data and r.data['labels']:
                lbl_data = r.data['labels'][0]
                lbl = lbl_data.get('name', 'Unknown')
                cat = lbl_data.get('catno', '')
            
            title_str = r.title
            if len(title_str) > 45: title_str = title_str[:45] + ".."
            
            print(f"   [{i+1}] {title_str} ({yr}) - {lbl} [{cat}]")
            found_releases.append(r)
            
    except Exception as e:
        print(f"   ⚠️ Error displaying results: {e}")
        return None, None
        
    if not found_releases:
        print("   ❌ No readable results found.")
        return None, None

    print("   Select # (or 0 to cancel): ", end="", flush=True)
    choice = sys.stdin.readline().strip()
    
    if choice == '0' or not choice.isdigit():
        print("   Cancelled.")
        return None, None
        
    choice_idx = int(choice) - 1
    if 0 <= choice_idx < len(found_releases):
        return found_releases[choice_idx], backup_id_prefix
    else:
        print("   Invalid selection.")
        return None, None

def is_duplicate(check_id):
    """Checks if UPC exists in SQLite database."""
    try:
        conn = sqlite3.connect(DB_FILE)
        cursor = conn.cursor()
        
        # Normalize input: strip leading zeros just in case
        clean_input = str(check_id).strip().lstrip('0')
        
        # We check both exact match and stripped match
        # This solves the 0777 vs 777 issue
        cursor.execute("SELECT UPC FROM albums")
        all_upcs = [row[0] for row in cursor.fetchall()]
        
        conn.close()
        
        # Python-side check for flexibility
        for db_upc in all_upcs:
            if str(db_upc).strip().lstrip('0') == clean_input:
                return True
                
        return False
    except Exception as e:
        print(f"   ⚠️ DB Check Warning: {e}")
        return False

def save_to_db(release, unique_id, retry=False):
    # Saves release and tracks to SQLite
    conn = sqlite3.connect(DB_FILE)
    cursor = conn.cursor()
    
    try:
        # Insert Album
        genre_str = ", ".join(release.genres) if release.genres else ""
        styles_str = ", ".join(release.styles) if release.styles else ""
        artist = ", ".join([a.name for a in release.artists])
        sort_artist = generate_sort_artist(artist)      # generate SortArtist for DB
        title = release.title
        year = str(release.year) if hasattr(release, 'year') else ''
        fmt = ", ".join([f['name'] for f in release.formats]) if release.formats else 'Unknown'
        label = release.labels[0].name if release.labels else 'Unknown'

        cursor.execute('''
            INSERT INTO albums (UPC, Artist, SortArtist, Title, Year, Format, Label, Genre, Styles)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
        ''', (unique_id, artist, sort_artist, title, year, fmt, label, genre_str, styles_str))

        # Insert Tracks
        track_data_list = []
        extract_track_data(unique_id, release.tracklist, None, track_data_list)
        
        for t in track_data_list:
            cursor.execute('''
                INSERT INTO tracks (UPC, Position, Parent_Work, Track_Title, Duration)
                VALUES (?, ?, ?, ?, ?)
            ''', (t['UPC'], t['Position'], t['Parent_Work'], t['Track_Title'], t['Duration']))

        conn.commit()
        print(f"✅ Saved: {artist} - {title}")
        return True

    except sqlite3.IntegrityError:
        print(f"   ⚠️  Duplicate ID detected during save.")
        return False
    except Exception as e:
        print(f"   ⚠️  Error saving to DB: {e}")
        return False
    finally:
        conn.close()

def extract_track_data(upc, track_list_source, parent_title, output_list):
    """Recursively extracts tracks."""
    for track in track_list_source:
        if hasattr(track, 'sub_tracks') and track.sub_tracks:
            current_work_title = track.title
            extract_track_data(upc, track.sub_tracks, current_work_title, output_list)
        else:
            output_list.append({
                'UPC': upc,
                'Position': track.position,
                'Parent_Work': parent_title if parent_title else "", 
                'Track_Title': track.title,
                'Duration': track.duration
            })

if __name__ == "__main__":
    main()