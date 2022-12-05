using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace deepduplicates
{
    public class CleanupHandler
    {
        private VideoInfoContext db { get; set; }
        private FileHandler fh { get; set; }

        public CleanupHandler(VideoInfoContext db, FileHandler fh){
            this.db = db;
            this.fh = fh;
        }

        public async Task runCleanup(){
            int counter = 0;

            Console.WriteLine("Resolving all files first...");
            List<string> allFiles = fh.allFiles.ToList();
            int removedEntries = 0;

            Console.WriteLine("Reading all entried in DB.");
            int dbEntries = db.VideoInfos.Count();
            foreach (VideoInfo item in db.VideoInfos){
                counter++;
                
                if (counter == 1 || counter % 100 == 0) Console.WriteLine(counter + "/"+ dbEntries + "  looking for: " + item.path);

                // Deleting the "remove" bit from all files, ensuring that these are all re-processed
                if (item.remove ?? false) item.remove = null;

                string file = allFiles.FirstOrDefault(x =>x == item.path);
                if (file == null)  {
                    Console.WriteLine("NOT FOUND: " + item.path);
                    removedEntries++;
                    fh.deleteScreenshots(item);
                    db.VideoInfos.Remove(item);
                    Console.WriteLine("DB entry and screenshots deleted for id: " + item.id );
                } else {
                    allFiles.Remove(file);
                }
            }
            await db.SaveChangesAsync();
            Console.WriteLine("Removed number of entries: " + removedEntries);
        }
    }
}