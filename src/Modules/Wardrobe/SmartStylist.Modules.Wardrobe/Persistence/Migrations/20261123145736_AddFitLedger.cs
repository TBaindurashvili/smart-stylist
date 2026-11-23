using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SmartStylist.Modules.Wardrobe.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFitLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "wardrobe");

            migrationBuilder.CreateTable(
                name: "fit_entries",
                schema: "wardrobe",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    brand = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    size = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    verdict = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    recorded_on = table.Column<DateOnly>(type: "date", nullable: false),
                    kept = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fit_entries", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_fit_entries_brand",
                schema: "wardrobe",
                table: "fit_entries",
                column: "brand");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fit_entries",
                schema: "wardrobe");
        }
    }
}
