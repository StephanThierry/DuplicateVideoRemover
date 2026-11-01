using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace deepduplicates
{
    // -- To update db  --
    //dotnet build
    //dotnet ef migrations add [migration_name] - fx. "InitialCreate" 
    //dotnet ef database update
    // Lower-case name kept on purpose: it has been part of the engine since the start
#pragma warning disable CS8981
    public class rgb {
#pragma warning restore CS8981
        public int r { get; set; }
        public int g { get; set; }
        public int b { get; set; }

        // Used in the match reason shown in the report (was printed as "deepduplicates.rgb")
        public override string ToString() => $"{r}/{g}/{b}";
    }
    
    public class VideoInfoContext : DbContext
    {
        public DbSet<VideoInfo> VideoInfos { get; set; }

        // The database may be in its own folder (see DataLocation). Wait up to 60 s when another
        // program (a second app or a scan elsewhere) is writing at the same moment.
        protected override void OnConfiguring(DbContextOptionsBuilder options)
    => options.UseSqlite($"Data Source={DataLocation.DatabasePath};Default Timeout=60");

        /// <summary>
        /// Creates the database if needed and sets the classic journal mode instead of WAL. WAL does not work
        /// when the database is opened over a network share (a PC using the NAS's database), and the classic
        /// mode still lets several programs read while one writes. Call once at start-up.
        /// </summary>
        public static void Prepare()
        {
            using var db = new VideoInfoContext();
            db.Database.EnsureCreated();
            try
            {
                // Can only change while no other program has the database open; it's stored in the file,
                // so it only has to succeed once
                db.Database.SetCommandTimeout(5);
                db.Database.ExecuteSqlRaw("PRAGMA busy_timeout=5000; PRAGMA journal_mode=DELETE;");
            }
            catch (Exception e)
            {
                Console.WriteLine("Database journal mode not changed (in use by another program): " + e.Message);
            }
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Paths are stored in Windows form and converted to the current OS when read
            // (see PathMapper / pathmapping.json), so one database works on Windows and Linux.
            modelBuilder.Entity<VideoInfo>()
                .Property(v => v.path)
                .HasConversion(
                    local => PathMapper.ToStorage(local),
                    stored => PathMapper.ToLocal(stored));
        }

    }

    public class VideoInfoWrapper{ 
        public VideoInfo item { get; set; }
        public string path { get; set; }
    }
    
    public class VideoInfo
    {
        public int id { get; set; }
        public int? duration { get; set; }
        public long? fileSize { get; set; }
        public double? bitrate { get; set; }
        public string fileHash { get; set; }

        public string image1Checksum_json
        {
            get
            {
                return JsonConvert.SerializeObject(image1Checksum);
            }

            set
            {
                image1Checksum = value == null ? null : JsonConvert.DeserializeObject<rgb[]>(value);
            }
        }
        
        public string image2Checksum_json
        {
            get
            {
                return JsonConvert.SerializeObject(image2Checksum);
            }

            set
            {
                image2Checksum = value == null ? null : JsonConvert.DeserializeObject<rgb[]>(value);
            }
        }

        public string image3Checksum_json
        {
            get
            {
                return JsonConvert.SerializeObject(image3Checksum);
            }

            set
            {
                image3Checksum = value == null ? null : JsonConvert.DeserializeObject<rgb[]>(value);
            }
        }

        [NotMapped]
        public rgb[] image1Checksum { get; set; }
        [NotMapped]
        public rgb[] image2Checksum { get; set; }
        [NotMapped]
        public rgb[] image3Checksum { get; set; }
        public string path { get; set; }
        public bool formatNotSupported { get; set; }
        public string reason { get; set; }
        public bool? remove { get; set; }
        public byte[] image1hash_blob { get; set; }
        public byte[] image2hash_blob { get; set; }
        public byte[] image3hash_blob { get; set; }

        [NotMapped]
        public List<bool> image1hash { get; set; }
        [NotMapped]
        public List<bool> image2hash { get; set; }
        [NotMapped]
        public List<bool> image3hash { get; set; }
        [NotMapped]
        public int triggerId { get; set; }

        /// <summary>Copy of the fields (the screenshot hashes are shared, they are never changed).</summary>
        public VideoInfo ShallowCopy() => (VideoInfo)MemberwiseClone();

        public override string ToString()
        {
            return ("Duration: " + duration.ToString().PadLeft(7) + " : " + this.path);
        }

    }
}