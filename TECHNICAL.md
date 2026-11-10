# Technical notes

Facts about how the program works that are easy to get wrong. Read this before changing anything that
reads the database.

## The database is a cache, not a list of files that exist

`deepdupes.db` holds every video file that has **ever** been read, keyed by path. It is used for one thing:
during a scan, a file whose path is already in the database is not read again ("I've seen this exact file
before"), which is what makes later scans fast.

It is **not** a reliable list of the files that exist now, and it is expected to contain many rows that no
longer point at a real file ("dormant" rows):

- When a folder is moved or renamed, the next scan finds the files under their new paths and adds new rows.
  The rows under the old paths stay. The same video is now in the database twice: once dormant, once current.
- When a file is deleted outside the program, its row stays.
- When a drive or share is not reachable during a scan (NAS asleep, VPN down, drive letter not mapped),
  its files are simply not found. Their rows stay - and must stay, because the files are probably fine.

Because of the last point, the command-line `clean` mode (CleanupHandler.cs), which deletes every row whose
file the current scan did not find, is dangerous: one unreachable share wipes thousands of rows, and every
one of those videos has to be read again. Do not run it automatically, and do not build features on it.

### What to use instead

- **The files the last scan found** - `LastScan` (Core/LastScan.cs), saved as `output/lastscan.json` in the
  data folder each time a scan has listed the folders. It is per installation, because which drives are
  reachable differs per computer. Anything that needs "the videos that exist now" takes the database rows
  whose path is in this list: `AppController.CurrentMedia()`. This is used by "Compare again" and by
  Re-encode. Without the filter, a moved folder's dormant rows would be matched with the current rows as
  duplicates, and the review could suggest deleting the real file.
- **A check on disk** - when the encoding script is written, the picked files are checked again
  (`LastScan.Exist`) and missing ones are left out and reported. If there is no `lastscan.json` yet (first
  start after an update), `LastScan.Filter` falls back to checking every row on disk. That is slow on network
  shares (minutes for 100,000 rows), so the result is kept until the data changes; the next scan replaces it
  with the scan list. Checking every listed file on each load was tried and was too slow for the same reason.
- The scan itself works on the files it found (`VideoHandler.saveVideoMetadataAndScreenshots` joins the
  file listing with the database), so its results are always current.

Rows are only deleted from the database when the program deletes the file itself (Review, Re-encode).

### A row is read again when the file at its path changes

Because rows are keyed by path, a file that is *replaced* would keep the row of the old file: its duration, size and the
three screenshots (`_screens/<row id>_<n>.png`, reused whenever they exist). The typical case is the re-encode queue: an incomplete
`..._720p.mp4` (an interrupted run) is scanned, then deleted and encoded again under the same name - the row still says "47 seconds, 4 MB"
and the finished 1400-second file never matches its original.

So the folder listing (`FileHandler.FindFiles`) keeps each file's size (`allFileSizes`; the listing reports it for free) and the scan
compares it with `fileSize` of the row (`VideoHandler.IsStale`). A different size means another file is there: the row is cleared,
including its pictures (`ForgetWhatWasRead`), and read again like a new file. Rows with no size (never read, -1, 0) are read anyway.
Stored sizes were checked against the disk for 67,000 rows: only the stale ones differed.

## The encode queue

`EncodeEngine` (Core/EncodeEngine.cs) is owned by `AppController.Encoder`, so there is one queue per process and
the web and desktop screens only subscribe to it (`ProgressChanged`, an `EncodeProgress` snapshot, a few times a
second from a background thread - screens coalesce it onto their UI thread).

- **One video at a time.** Videos are added at any time (`Enqueue`, also while it runs) and run in order.
  Statuses: Queued, Running, Done, Failed, Skipped. `Stop` puts the running video back as Queued and halts;
  `Start` resumes; `SkipCurrent` marks the running one Skipped; `Remove` / `ClearQueued` / `ClearFinished` / `Retry`.
  Adding while halted does not start it. A path that is queued, running or done is not added twice.
- **FFmpeg is run directly**, not through a script: `EncodePlanner.Arguments(input, settings, partOutput)` is the same
  argument list the scripts use, plus `-progress pipe:1 -nostats -nostdin`. Progress comes from the `key=value`
  blocks on stdout (`FfmpegProgressParser`: `out_time_us`, `fps`, `speed`, `total_size`; early blocks hold `N/A`).
  Percent is `out_time` over the duration already in the database. stderr goes to the log and the failure message.
- **`.part` files.** The output is written to `<new name>.part` with `-f mp4|matroska` named explicitly, and renamed
  when FFmpeg exits with 0. The `.part` extension keeps a scan that runs at the same time from picking up half a
  video, which is why scans and encodes are not made to exclude each other. A stop, skip or failure deletes the
  `.part` file (with retries: FFmpeg may still hold it). Stale ones left by a crash are deleted at the next start
  and before each video is encoded.
- **The queue is saved** to `output/encodequeue.json` (data folder, per installation, like `lastscan.json`) on
  every status change, written to a temp file and moved into place. After a restart a video that was running is
  Queued again and nothing starts by itself (`Halted`); the web app calls `Start()` when it has started, because a
  server has nobody to press the button. The saved state does not remember a deliberate Stop, so a restart after
  a Stop carries on in the web app. At most 300 finished videos are kept.
- **The new file is checked** (`EncodeChecker`, Core/EncodeCheck.cs), both when it was there already and right after an
  encode. `ffprobe` gives the length (within 1% / 2 s of the original's, from the database) and a video stream; then
  `ffmpeg -ss <t> -frames:v 1 -vf scale=240:160` makes pictures at the scan's points (length/4, /2, 3/4 - the same code
  as `VideoHandler.generateOneSetOfScreenshotsAsync`) and `ImageHandler.imageChecksum` proves they decode. A fourth
  picture from the last second is made and thrown away: an mp4 cut off at 60% still has its header (and so its length)
  and still gives pictures at 25% and 50%, only the end gives it away (a test covers this with real FFmpeg).
  Results: Done / **AlreadyEncoded** (good) or **Incomplete** (bad); the three pictures are kept as
  `output/encode_checks/<item id>_<n>.png` for the screens and removed with the item. Statuses are saved as numbers:
  only ever add new ones at the end of `EncodeItemStatus`. Nothing is ever deleted by the engine itself.
- **Deleting an original / the new file / replacing an incomplete file** are `AppController.DeleteOriginalAfterEncode`,
  `DeleteNewFile` (the row stays: `NewDeleted` greys the new file out and the video is planned again; `OriginalDeleted` does
  the same for the original, and each copy keeps its buttons while it exists - the dialog says when it is the last one)
  and `ReplaceIncomplete`, one video at a time and only for what the queue item records: the original only when the check
  passed and the new file is still the size that was checked; the replace only for the new file of an Incomplete item.
  Both use `RecycleBin` (Core/RecycleBin.cs): the Windows Recycle Bin (through `Microsoft.VisualBasic.FileIO`) for fixed
  drives, `gio trash` / `trash-put` on Linux. A network path or USB drive has none, and the shell would delete there
  without saying so, so `IsRecoverable` is asked first and the confirmation says "permanently" when it is false.
  Deleting the original also removes its database row and screenshots, like the other delete in Re-encode.
- **Items from before the check existed** (a Done item, or a skipped "already exists") are checked in the background when the program starts (EncodeEngine.CheckUncheckedAsync, started by AppController), and so are items whose pictures have gone missing. They then show like the others: side by side, with the saving and the delete icons. A skipped one becomes Already encoded or Incomplete; their finish times and order stay as they were.
- **Per video settings.** `EncodeSettings` are cloned into each queue item, so later changes do not alter what waits.
- **`encode.lock`** (next to `encodequeue.json`; the `ScanLock` mechanism with a heartbeat) stops two programs that
  share a data folder from working the same queue. The encoder does not touch the database.
- **Time left** is the speed of this run so far (video seconds encoded per second of work, the running video counted as
  far as it got) applied to the video seconds left, so long videos weigh more than short ones.
- **Tests.** `tests/CoreTests` (`dotnet test tests/CoreTests`): the parser, the arguments, and the engine with a fake
  runner (`IEncodeRunner`); two tests encode and stop a real clip when `ffmpeg/ffmpeg(.exe)` exists in the repo.
  `SHOT_QUEUE=1` in `tests/DesktopShots` runs the whole thing through the desktop UI (see Program.cs).

## Selecting copies for deletion (Review)

`ResultSession` keeps, per group, which copy is kept (`keep`) and a set of selected files. The buttons that select many files
(all duplicates, a folder) always leave one copy per group. Ticking files **by hand** may select every copy of a video:
`SetSelected` allows it and returns a warning ("Both copies of this video are selected...") that the screens show as a toast.
The group is then "doomed" (`IsDoomed`, `GroupsFullySelected`): all its rows turn red with a "Every copy will be deleted" mark, nothing
shows as kept, and the confirmation says that every copy of N videos goes. Unselecting one copy makes it the kept one again, so
`keep` is never a selected file unless every copy is selected.

## Recognising a video when its path changes

Paths change (moves, renames, drive letters), so anything that must follow a video across moves uses a
content fingerprint instead of the path or the database id: `FalsePositives.Fingerprint` - size, length
and the three screenshot hashes. The false-positive marks (`falsepositives.json`, next to the database)
use it. Database ids are not stable either: a moved file gets a new row and a new id.

## Where files are kept

| File | Folder | What |
|---|---|---|
| `settings.json`, `pathmapping.json` | data folder | settings, Windows/Linux path pairs and synonyms |
| `output/results.json` | data folder | the last results shown in Review |
| `output/lastscan.json` | data folder | the files the last scan found on disk |
| `output/encodequeue.json`, `output/encode.lock` | data folder | the encode queue, and who is working it |
| `deepdupes.db`, `_screens/` | database folder | the read cache and screenshots (named by row id) |
| `runhistory.json` | database folder | scan timings for estimates |
| `falsepositives.json` | database folder | pairs marked "False positive", by fingerprint |

The data and database folders are the same unless `--db` / `DVR_DB` is given (see DataLocation.cs).
