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
    public class rgb {
        public int r { get; set; }
        public int g { get; set; }
        public int b { get; set; }
    }
    
    public class VideoInfoContext : DbContext
    {
        public DbSet<VideoInfo> VideoInfos { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder options)
    => options.UseSqlite("Data Source=deepdupes.db");

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
                image1Checksum = JsonConvert.DeserializeObject<rgb[]>(value);
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
                image2Checksum = JsonConvert.DeserializeObject<rgb[]>(value);
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
                image3Checksum = JsonConvert.DeserializeObject<rgb[]>(value);
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

        public override string ToString()
        {
            return ("Duration: " + duration.ToString().PadLeft(7) + " : " + this.path);
        }

    }
}