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

**The desktop and web apps** have three screens:
* **Settings** - folders to scan, folders to skip, folders whose copies are preferred, the Windows and Linux path pairs (with a "try a path" box), and the match rules as sliders. A live preview shows how many duplicates the settings would find, using the videos already read.
* **Scan** - progress through the steps, videos found, already read and new, and the time left. The estimate comes from earlier scans until the current one has read a few videos. A scan can be stopped; what was read is kept.
* **Review** - each group of copies with 3 screenshots per video. Select all duplicates, select none, select one by one, "Keep this one", or pick a folder and "Select all duplicates in this folder". Files picked for removal are crossed out. Remove them by moving them to a quarantine folder (`_DuplicateVideoRemover_quarantine` inside the scanned folder) or by deleting them; removed files disappear and you can carry on. A group always keeps at least one copy.

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
| `/videos` | your video folders, mounted below it. Must be writable to move or delete duplicates. |

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
removing files works while a scan runs elsewhere. The database uses SQLite's classic journal mode, which
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
