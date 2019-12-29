## DuplicateVideoRemover - Video Duplicate Finder 

.NET Core 3.0 project that will locate:
1. Video duplicates
2. Invalid video files 
3. Videos at or under a specified length (2 sec by default)

Delete commands for all files found are added to a batchfile in the /output folder
An HTML report of the findings are made and placed in the output folder.  

Review the report before running the batch file to delete, the algorithms are about 99.2% accurate - so 2-3 in each 1000 videos will likely be a false positive.   

All settings are located in: `settings.json`  - run the program once to create this.  

### To run
`dotnet restore`  
`dotnet run` (First time it's just to create the settings file)   
Edit `settings.json`  
* Set `contentFolders` to the folders you want to index - notice on windows blackslashes must be double so `d:\video` must be written `d:\\video` - if not, the JSON will be invalid.   
* Set `minVideoLength` - default 2 sec  
Now you can re-start the program:   
`dotnet run` 

### How it works
1. The program first indexes all video-files in the folders specified in "contentFolders" in settings.json and reads the video metadata (size and duratation)  
Each entry is saved in a SQLite DB so you can stop the program at any time and it will resume where it left off within a few seconds or minutes, depending on the hardware and the amount of files in the indexed folders.  

It's recommneded to place DuplicateVideoRemover on an SSD disk since it executes a lot of local read/write operations to update the state of the program. The free space should be aprox. 360 Kb pr video, so if you plan on running the program om 20.000 videos, make sure your SSD has more than 7 GB of free space.   

2. All videos that match other videos in duration will processed again and 2 screenshots of each of these will be taken.  

3. After all screenshots are taken, checksums are calculated, compared and a .bat file and an HTML report is made.  

**Dependencies**
Xabe.FFmpeg  
FFmpeg (Xabe will automaticalæly download it)  
SQLite  
