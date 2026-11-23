using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SmartStylist.Modules.Catalog.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BrowserWatchEgressSanity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "dropped",
                schema: "catalog",
                table: "shops",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "prices_rejected",
                schema: "catalog",
                table: "collection_runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "watched_products",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    shop_id = table.Column<int>(type: "integer", nullable: false),
                    url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    size = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_watched_products", x => x.id);
                    table.ForeignKey(
                        name: "fk_watched_products_shops_shop_id",
                        column: x => x.shop_id,
                        principalSchema: "catalog",
                        principalTable: "shops",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_watched_products_shop_id_url",
                schema: "catalog",
                table: "watched_products",
                columns: new[] { "shop_id", "url" },
                unique: true);

            // PriceSource.Manual was renamed ManualHistory.
            migrationBuilder.Sql("UPDATE catalog.price_snapshots SET source = 'ManualHistory' WHERE source = 'Manual';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE catalog.price_snapshots SET source = 'Manual' WHERE source = 'ManualHistory';");

            migrationBuilder.DropTable(
                name: "watched_products",
                schema: "catalog");

            migrationBuilder.DropColumn(
                name: "dropped",
                schema: "catalog",
                table: "shops");

            migrationBuilder.DropColumn(
                name: "prices_rejected",
                schema: "catalog",
                table: "collection_runs");
        }
    }
}
