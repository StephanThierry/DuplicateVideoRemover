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
| `deepdupes.db`, `_screens/` | database folder | the read cache and screenshots (named by row id) |
| `runhistory.json` | database folder | scan timings for estimates |
| `falsepositives.json` | database folder | pairs marked "Not a duplicate", by fingerprint |

The data and database folders are the same unless `--db` / `DVR_DB` is given (see DataLocation.cs).
