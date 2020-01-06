## DuplicateVideoRemover - Video Duplicate Finder 

.NET Core 3.1 project that will locate:
1. Video duplicates
2. Invalid video files 
3. Videos at or under a specified length (2 sec by default)

Delete commands for all files found are added to a batchfile (delete_all_dupes.bat) in the /output folder
An HTML report (report.html) of the findings are made and placed in the output folder.  

Review the report before running the batch file to delete, the algorithms are about 99.99% accurate - so 1-2 in each 10000 videos will likely be a false positive.   

All settings are located in: `settings.json`  - run the program once to create this file.  

### To run
Requires .NET Core 3.1 (or higher) https://dotnet.microsoft.com/download - so install that first.  
`dotnet restore`  
`dotnet run` (On first run it created the database, downloads Ffmpeg, creates the settings file and then exits the program)   
Edit `settings.json`  
* Set `contentFolders` to the folders you want to index - notice on windows blackslashes must be double  
  so `d:\user\videos` must be written `d:\\user\\videos` - if not, the JSON will be invalid.   
* Set `minVideoLength` - default 3 sec. It will add all videos that are 2 sec or less in duration to the delete-list.    

Now you can re-start the program:   
`dotnet run` 

### How it works
1. The program first indexes all video-files in the folders specified in "contentFolders" in settings.json and reads the video metadata (size and duration) and 3 screenshots are made at 25%, 50% and 75%.
The screenshots are saved in the `_screens`-folder and the metadate is saved in a SQLite DB. This means you can stop the program at any time, it will resume where it left off within a few seconds or minutes, depending on the hardware and the amount of files in the indexed folders.  It will remember each file it indexs on the path so you have to delete the .db file to reset the program to zero. If you do, you must also delete the content of the "_screens" folder, since it will not match the indexes of a new database.

It's recommneded to place the program on an SSD disk since it executes a lot of local read/write operations to update the db and read/write the screenshots. The free space should be aprox. 260 Kb pr video, so if you plan on running the program om a NAS with 43.000 videos, make sure your SSD has more than 10 GB of free space.   

2. After the indexing process is over, checksums are calculated, compared and a .bat file and an HTML report is made.  

**Dependencies**  
Xabe.FFmpeg - at the moment running on a custom build located in the `/components`-folder. After pending pull request will switch back to package (master).  
FFmpeg (Latest version is automatically downloaded on first run)  
