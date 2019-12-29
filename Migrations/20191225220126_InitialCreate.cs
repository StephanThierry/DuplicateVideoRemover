using Microsoft.EntityFrameworkCore.Migrations;

namespace deepduplicates.Migrations
{
    public partial class InitialCreate : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VideoInfos",
                columns: table => new
                {
                    id = table.Column<int>(nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    duration = table.Column<int>(nullable: true),
                    fileSize = table.Column<long>(nullable: true),
                    fileHash = table.Column<string>(nullable: true),
                    image1Checksum = table.Column<long>(nullable: true),
                    image2Checksum = table.Column<long>(nullable: true),
                    path = table.Column<string>(nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VideoInfos", x => x.id);
                });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VideoInfos");
        }
    }
}
