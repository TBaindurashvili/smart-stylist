using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SmartStylist.Modules.Wardrobe.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StyleProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_fit_entries_brand",
                schema: "wardrobe",
                table: "fit_entries");

            migrationBuilder.AddColumn<long>(
                name: "profile_id",
                schema: "wardrobe",
                table: "fit_entries",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "style_profiles",
                schema: "wardrobe",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    shops_for = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    liked_styles = table.Column<List<string>>(type: "text[]", nullable: false),
                    avoided_styles = table.Column<List<string>>(type: "text[]", nullable: false),
                    wanted_categories = table.Column<List<string>>(type: "text[]", nullable: false),
                    no_go = table.Column<List<string>>(type: "text[]", nullable: false),
                    loved_brands = table.Column<List<string>>(type: "text[]", nullable: false),
                    skipped_brands = table.Column<List<string>>(type: "text[]", nullable: false),
                    goal = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    top_size = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    bottom_size = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    shoe_size = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_style_profiles", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_fit_entries_profile_id_brand",
                schema: "wardrobe",
                table: "fit_entries",
                columns: new[] { "profile_id", "brand" });

            migrationBuilder.CreateIndex(
                name: "ix_style_profiles_name",
                schema: "wardrobe",
                table: "style_profiles",
                column: "name",
                unique: true);

            // Fits recorded before profiles existed were the first user's: give them a profile called "Me"
            // (renamed in the quiz) so they keep their size advice.
            migrationBuilder.Sql("""
                INSERT INTO wardrobe.style_profiles (name, shops_for, liked_styles, avoided_styles, wanted_categories, no_go, loved_brands, skipped_brands, updated_at)
                SELECT 'Me', 'Both', '{}', '{}', '{}', '{}', '{}', '{}', now()
                WHERE EXISTS (SELECT 1 FROM wardrobe.fit_entries);
                UPDATE wardrobe.fit_entries SET profile_id = (SELECT min(id) FROM wardrobe.style_profiles);
                """);

            migrationBuilder.AddForeignKey(
                name: "fk_fit_entries_style_profiles_profile_id",
                schema: "wardrobe",
                table: "fit_entries",
                column: "profile_id",
                principalSchema: "wardrobe",
                principalTable: "style_profiles",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_fit_entries_style_profiles_profile_id",
                schema: "wardrobe",
                table: "fit_entries");

            migrationBuilder.DropTable(
                name: "style_profiles",
                schema: "wardrobe");

            migrationBuilder.DropIndex(
                name: "ix_fit_entries_profile_id_brand",
                schema: "wardrobe",
                table: "fit_entries");

            migrationBuilder.DropColumn(
                name: "profile_id",
                schema: "wardrobe",
                table: "fit_entries");

            migrationBuilder.CreateIndex(
                name: "ix_fit_entries_brand",
                schema: "wardrobe",
                table: "fit_entries",
                column: "brand");
        }
    }
}
