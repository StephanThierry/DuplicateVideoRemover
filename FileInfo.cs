using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace deepduplicates
{
    // -- To update db  --
    //dotnet build
    //dotnet ef migrations add [migration_name] - fx. "InitialCreate" 
    //dotnet ef database update

    public class VideoInfoContext : DbContext
    {
        public DbSet<VideoInfo> VideoInfos { get; set; }
    
        protected override void OnConfiguring(DbContextOptionsBuilder options)
    => options.UseSqlite("Data Source=deepdupes.db");

    }

    public class VideoInfo
    {
        public int id { get; set;   }
        public int? duration { get; set; }
        public long? fileSize { get; set; }
        public string fileHash { get; set; }
        public long? image1Checksum { get; set; }
        public long? image2Checksum { get; set; }
        public long? image3Checksum { get; set; }
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

        public override string ToString()
        {
            return ("Duration: " + duration.ToString().PadLeft(7) + " : " + this.path);
        }

    }
}