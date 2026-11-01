## DuplicateVideoRemover - Video Duplicate Finder 

.NET 10 program for Windows and Linux, with a desktop app, a web app and a command-line version, that will locate:
1. Video duplicates
2. Invalid video files 
3. Videos at or under a specified length (2 sec by default)
4. Generate a HTML report `./output/report.html` of all videofiles that are invalid or duplicates
5. Generate a script `./output/delete_all_dupes.ps1` (Windows PowerShell) or `./output/delete_all_dupes.sh` (Linux) with delete commands for all invalid and duplicate videos 
4. Is able to generate a complete HTML index `./output/reportIndex.html` of all videofiles with full path and 3 screenshots pr. video.

Review the report before running the delete script, the algorithms are about 99.99% accurate - so 1-2 in each 10000 videos will likely be a false positive.   

All settings are located in: `./settings.json`  - run the program once to create this file.  

### Three ways to use it
All three use the same engine and the same data folder (`settings.json`, `pathmapping.json`, `deepdupes.db`, `_screens`), so you can switch between them.

| | What it is | Start it |
|---|---|---|
| **Desktop app** | Window app for Windows and Linux | `dotnet run --project apps/Desktop -- --data <data folder>` |
| **Web app** | Runs on the computer with the videos (also a Linux server or NAS without a screen); use it from any browser | `dotnet run --project apps/Web -- --data <data folder>` |
| **Command line** | The original program: scans and writes a report and a delete script | `cd <data folder>` then `dotnet run --project <repo>/apps/Cli` |

Requires .NET 10 (or higher) https://dotnet.microsoft.com/download.  
On Linux, also install FFmpeg (`sudo apt install ffmpeg`). If it is installed, it's used instead of downloading a copy.

**The desktop and web apps** have these screens:
* **Settings** - folders to scan, folders to skip, folders whose copies are preferred, the Windows and Linux path pairs (with a "try a path" box), and the match rules as sliders. "Review false positives" lists the pairs marked as "False positive" (with where the videos are now) and lets you remove a mark again.
* **Scan** - the folders to scan as tags and the number of skip rules, then progress in two parts, each with its own bar and time left: A) finding the files - measured against how many files the last scan of the same folders found (on the first scan the total is unknown, so the bar just moves), and B) processing - once all files are known, the new ones are read and everything is compared. The time left for reading comes from earlier scans until the current one has read a few videos. A scan can be stopped; what was read is kept.
* **Review** - each group of copies with 3 screenshots per video, and a folder list starting with "All folders". Tick the files to delete (the copy marked Keep is the one left; ticking it keeps another copy instead), use Select all duplicates or select none, or pick a folder and "Select all duplicates in this folder". Files picked for removal are crossed out, and the size selected for deleting is shown at the bottom. The play button next to the screenshots opens a video in the default player (desktop) or in a player in the browser (web; browsers play mp4, webm and mov, not every format). Delete with one button (after a confirmation); a progress bar follows the deleting, and files that could not be deleted are listed with the reason. A group always keeps at least one copy.
* **All files** - every video the last scan found (it opens the file list the scan saved - nothing is scanned, so it works right after starting the app; "Show all files from …" on the Scan screen goes there too), with its screenshots at full size, play, "Exclude folder", "Copy path" and "Delete". Search the paths with words: `vacation lisbon 2026 -ocean` shows paths that contain the first three words but not "ocean" (not case sensitive; `"two words"` is a phrase). Fast with 60,000+ files.
* **False positive** - between two matched copies is a "False positive" button. When they only look alike, it takes the copy below the button out of its group and remembers the pair in `falsepositives.json` next to the database. Videos are recognised by their content (size, length and screenshot hashes), not their path, so the mark still applies after files are moved or renamed and scanned again.

**Re-encoding:** the **Re-encode** screen lists the videos that aren't duplicates or broken, highest average bitrate first (those shrink the most). Pick videos (or "Select top 20") and press "Create encoding script" to get `output/encode_selected.ps1` (Windows) or `.sh` (Linux) with one FFmpeg command per video. The new files are saved next to the originals with a name ending like `_1080p_x265`; the originals are not changed. Nothing is encoded until you run the script. Only files the last scan found on disk are listed (the database also remembers moved and missing files - see [TECHNICAL.md](TECHNICAL.md)), and picked files are checked again when the script is made. Each video has "Exclude folder" (hides that folder until "Unhide folders"), "Copy path" and "Delete" (after a confirmation).

How it encodes is set under **Settings > Re-encoding**, each with a recommended default:

| Setting | Choices | Default |
|---|---|---|
| Video format | H.265 (x265), H.264 (x264) | H.265 |
| Resolution | 720p, 1080p, 1440p (never enlarged, aspect ratio kept) | 1080p |
| Quality (CRF slider, right = better) | x265 16-34, x264 14-32 | x265: 22, x264: 20 (looks like the original) |
| Speed | very fast ... slower | medium |
| Sound | keep original, AAC 160 kbps | keep original |
| File type | MP4, MKV | MP4 |
| Apple devices (H.265 in MP4) | on/off | on |
| Name ending | any text | automatic, e.g. `_1080p_x265` |
| Never overwrite existing files | on/off | on |
| Leave out | file types, videos of N seconds or shorter | .mpg .mpeg .wmv, 2 s |
| Extra video filter (advanced) | FFmpeg filter, e.g. `yadif` | none |

These are stored as `encodeSettings` in `settings.json` (older files with `scale`, `compressionLevelcrf` and `compressionPreset` are converted). When that section exists, the command-line version also writes `output/encoding-template.ps1`/`.sh` with every video, highest bitrate first.

**Web app options**
* `--data <folder>` - the data folder (default: the current folder)
* `--urls http://0.0.0.0:5077` - listen on the network (default: this computer only, http://127.0.0.1:5077)
* `--token <secret>` - needed to open the page when listening on the network. If you leave it out, one is made up and printed with the address.
* `--no-browser` - don't open a browser

**Desktop app data folder:** `--data <folder>`, otherwise the current folder if it has a `settings.json`, otherwise the user's app data folder.

**Publishing a single program file:** `dotnet publish apps/Desktop -r win-x64 -c Release /p:PublishSingleFile=true --self-contained` (use `linux-x64` for Linux, and `apps/Web` or `apps/Cli` for the others).

### Running in Docker (for example on a Synology NAS)
The web app comes as a Docker image, with FFmpeg and the command-line version included.

1. Create a folder for the data, e.g. `/volume1/docker/duplicate-video-remover`.
2. Copy `docker-compose.yml` from this repository, change the video folders and the `user` line
   (your NAS user's id - over SSH: `id <user>`), and create a project with it in Synology's Container Manager
   (or run `docker compose up -d`).
3. Open `http://<nas>:8095/?token=<token>`. The token is in the container log and in `/data/.token`
   (set your own with `DVR_TOKEN`, or `DVR_TOKEN=off` on a network you trust).
4. In Settings, add the folders under `/videos` (e.g. `/videos/video`) and start a scan.

| Container path | What |
|---|---|
| `/data` | settings, database, screenshots, results. Map it to a folder on the NAS (or a named volume). |
| `/videos` | your video folders, mounted below it. Must be writable to delete duplicates. |

* Port: the web app listens on 8080 in the container; map it to any free port on the NAS (`8095:8080`).
* Command line inside the container: `docker exec duplicate-video-remover dvr-cli` (also `dvr-cli clean`, `dvr-cli index`).
* Build the image yourself: `docker build -t duplicate-video-remover .` (works for amd64 and arm64 NAS models).
  `.github/workflows/docker.yml` builds and publishes it to `ghcr.io/stephanthierry/duplicatevideoremover`
  on every push to master.

### Settings and database in separate folders
Every version takes two folders:
* `--data <folder>` (or `DVR_DATA`): settings, path mappings and output.
* `--db <folder>` (or `DVR_DB`): the database (`deepdupes.db`), the screenshots (`_screens`) and the scan history.
  Defaults to the data folder.

That way the container and the desktop or command-line version on a PC can use **the same database**
while each keeps its own settings - for example the container scans every night, and you review on your PC:

```
DuplicateVideoRemover.Desktop --data C:\DVR --db \\nas\docker\duplicate-video-remover
```

Add the path pairs (e.g. `\\nas\video\` and `/videos/video/`) under "Windows and Linux paths" in both, so the
same video has the same path in the database.

Only one scan runs per database at a time: a scan leaves a `scan.lock` file next to the database, and other
programs wait with their scan until it is gone (a lock left by a crash expires after 2 minutes). Reviewing and
deleting files works while a scan runs elsewhere. The database uses SQLite's classic journal mode, which
works over a network share; keep it on the NAS's own disk and let the PC reach it through the share.

### Command line
Run the command-line version from the data folder.  
`dotnet run --project apps/Cli` (On first run it creates the database, downloads Ffmpeg, creates the settings file and then exits the program)   
Edit `settings.json`  
* Set `contentFolders` to the folders you want to index - notice, on windows backslashes must be double  
  so `d:\user\videos` must be written `d:\\user\\videos` - if not, the JSON is not invalid.   
* Set `minVideoLength` - default 3 sec. It will add all videos that are 2 sec or less in duration to the delete-list.    

Now you can re-start the program:   
`dotnet run --project apps/Cli` 

### Running the scripts
On Windows the scripts in `./output` are PowerShell scripts (`.ps1`). Unlike `.bat` files they work with network paths that aren't mapped to a drive letter (`\\nas\video\...`), and with file names containing `%`, `[ ]`, `'` or `æøå`. Windows blocks scripts by default, so run them like this:

```powershell
powershell -ExecutionPolicy Bypass -File .\output\delete_all_dupes.ps1
```

### Running on Linux and sharing the database with Windows
The program runs the same way on Linux. The scripts in `./output` are then `.sh` files: run them with `./output/delete_all_dupes.sh`.  

To use the same database (`deepdupes.db` and the `_screens` folder) from both Windows and Linux, tell the program how your drives map to each other in `pathmapping.json` (created next to `settings.json` on first run):

```json
{
  "mappings": [
    { "windows": "L:\\", "linux": "/mnt/architecture/" },
    { "windows": "\\\\nas.protek.dk\\video\\", "linux": "/mnt/video/" }
  ]
}
```

* Paths are stored in the database in Windows form, and converted to Linux paths when the program runs on Linux (and back when it saves).  
* Paths in `settings.json` can be written in either form, so one `settings.json` works on both systems.  
* The Linux side must be a mounted folder (e.g. a CIFS mount in `/etc/fstab`). Network locations like `smb://nas/share` from a file manager can't be used directly; use the mount point instead (with GNOME/GVFS that's under `/run/user/<uid>/gvfs/`).  
* An existing Windows database works on Linux as-is once the mappings are in place.  
* Under each pair, Settings checks the pair against the database: it takes up to 10 stored paths from the other system's side, translates them with the pair and checks that the files are there (e.g. "10 of 10 files found").  

**Other names for the same place (synonyms)**  
A drive letter and the network path it points to are two names for the same folder, e.g. `L:\` and `\\nas\video\`. Add them under "Other names for the same place" in Settings, or in `pathmapping.json`:

```json
"synonyms": [ { "main": "\\\\nas\\video\\", "alias": "L:\\" } ]
```

* The main name (best: the network path, it works on every computer) is what the database uses. Paths under the alias are read and stored as the main name, so a workstation using `L:` and one using `\\nas\video` share the same database entries.
* On a computer that can't reach the main name but can reach the alias, the alias is used there.
* Settings shows how many videos in the database still use the alias and checks a sample of them under the main name. "Update paths" then rewrites them to the main name once; videos stored under both names become one entry.

### How it works
1. The program first indexes all video-files in the folders specified in "contentFolders" in settings.json and reads the video metadata (size and duration) and 3 screenshots are made at 25%, 50% and 75%.
The screenshots are saved in the `_screens`-folder and the metadate is saved in a SQLite DB. This means you can stop the program at any time, it will resume where it left off within a few seconds or minutes, depending on the hardware and the amount of files in the indexed folders.  It will remember each file it indexes on the path so you have to delete the .db file to reset the program to zero. If you do, you must also delete the content of the "_screens" folder, since it will no longer match the indexes of a new database.

It's recommneded to place the program on an SSD disk since it executes a lot of local read/write operations to update the db and read/write the screenshots. Free space on the drive must be aprox. 260 Kb pr video, so if you plan on running the program om a NAS with 43.000 videos, make sure your SSD has more than 10 GB of free space.   

2. After the indexing process is over, checksums are calculated, compared and a delete script (`.ps1` on Windows, `.sh` on Linux) and an HTML report is made in the `./output` folder. The report groups each original with its duplicates, shows broken or short videos separately, and can be filtered by path.  

**Cleaning the database and screenshots**  
After running the program for a number of days (or months) you may have a lot of references in the database and screenshots in the `_screens` folder that are nolonger present, either because they have been deleted or moved. You can clean up thoes entried and files by running the application with the `clean` parameter.  
Like so: `dotnet run --project apps/Cli -- clean`  
Both entries in the db and screenshors stored in the file system that don't match with a file currently found in the filessystem will be permanemtly deleted, so make sure you have access to all drive sources when you run the cleanup.  

**Making a full index report**  
If you need a complete index of all videos, you can generate a full index report.  
Like so: `dotnet run --project apps/Cli -- index`  
A HTML report `indexReport.html` is made in the `.\output` folder. 

**Dependencies**  
Xabe.FFmpeg - at the moment running on a custom build located in the `/components`-folder. After pending pull request will switch back to package (master).  
FFmpeg (Latest version is automatically downloaded on first run)  
