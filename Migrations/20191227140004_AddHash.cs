using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace deepduplicates.Migrations
{
    public partial class AddHash : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "image1hash_blob",
                table: "VideoInfos",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "image2hash_blob",
                table: "VideoInfos",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "image1hash_blob",
                table: "VideoInfos");

            migrationBuilder.DropColumn(
                name: "image2hash_blob",
                table: "VideoInfos");
        }
    }
}
