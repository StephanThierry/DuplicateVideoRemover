## DuplicateVideoRemover - Video Duplicate Finder 

.NET 10 project for Windows and Linux that will locate:
1. Video duplicates
2. Invalid video files 
3. Videos at or under a specified length (2 sec by default)
4. Generate a HTML report `./output/report.html` of all videofiles that are invalid or duplicates
5. Generate a script `./output/delete_all_dupes.bat` (Windows) or `./output/delete_all_dupes.sh` (Linux) with delete commands for all invalid and duplicate videos 
4. Is able to generate a complete HTML index `./output/reportIndex.html` of all videofiles with full path and 3 screenshots pr. video.

Review the report before running the batch file to delete, the algorithms are about 99.99% accurate - so 1-2 in each 10000 videos will likely be a false positive.   

All settings are located in: `./settings.json`  - run the program once to create this file.  

### To run
Requires .NET 10 (or higher) https://dotnet.microsoft.com/download - so install that first.  
On Linux, also install FFmpeg (`sudo apt install ffmpeg`). If it is installed, it's used instead of downloading a copy.  
`dotnet restore`  
`dotnet run` (On first run it created the database, downloads Ffmpeg, creates the settings file and then exits the program)   
Edit `settings.json`  
* Set `contentFolders` to the folders you want to index - notice, on windows backslashes must be double  
  so `d:\user\videos` must be written `d:\\user\\videos` - if not, the JSON is not invalid.   
* Set `minVideoLength` - default 3 sec. It will add all videos that are 2 sec or less in duration to the delete-list.    

Now you can re-start the program:   
`dotnet run` 

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

2. After the indexing process is over, checksums are calculated, compared and a delete script (`.bat` on Windows, `.sh` on Linux) and an HTML report is made in the `./output` folder. The report groups each original with its duplicates, shows broken or short videos separately, and can be filtered by path.  

**Cleaning the database and screenshots**  
After running the program for a number of days (or months) you may have a lot of references in the database and screenshots in the `_screens` folder that are nolonger present, either because they have been deleted or moved. You can clean up thoes entried and files by running the application with the `clean` parameter.  
Like so: `dotnet run -- clean`  
Both entries in the db and screenshors stored in the file system that don't match with a file currently found in the filessystem will be permanemtly deleted, so make sure you have access to all drive sources when you run the cleanup.  

**Making a full index report**  
If you need a complete index of all videos, you can generate a full index report.  
Like so: `dotnet run -- index`  
A HTML report `indexReport.html` is made in the `.\output` folder. 

**Dependencies**  
Xabe.FFmpeg - at the moment running on a custom build located in the `/components`-folder. After pending pull request will switch back to package (master).  
FFmpeg (Latest version is automatically downloaded on first run)  
