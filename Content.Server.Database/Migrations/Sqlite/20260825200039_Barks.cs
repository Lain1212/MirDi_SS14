using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class Barks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "voice_bark_id",
                table: "profile",
                type: "TEXT",
                nullable: false,
                defaultValue: "DefaultVoice");

            migrationBuilder.AddColumn<float>(
                name: "voice_bark_pitch",
                table: "profile",
                type: "REAL",
                nullable: false,
                defaultValue: 1f);

            migrationBuilder.AddColumn<float>(
                name: "voice_bark_pitch_var",
                table: "profile",
                type: "REAL",
                nullable: false,
                defaultValue: 0.05f);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "voice_bark_id", table: "profile");
            migrationBuilder.DropColumn(name: "voice_bark_pitch", table: "profile");
            migrationBuilder.DropColumn(name: "voice_bark_pitch_var", table: "profile");
        }
    }
}
