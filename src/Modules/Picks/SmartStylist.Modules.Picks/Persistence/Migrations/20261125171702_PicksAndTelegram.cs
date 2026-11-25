using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SmartStylist.Modules.Picks.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PicksAndTelegram : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "picks");

            migrationBuilder.CreateTable(
                name: "sent_picks",
                schema: "picks",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    profile_id = table.Column<long>(type: "bigint", nullable: false),
                    product_id = table.Column<long>(type: "bigint", nullable: false),
                    title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    price = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    score = table.Column<int>(type: "integer", nullable: false),
                    sent_on = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sent_picks", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "subscribers",
                schema: "picks",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    profile_id = table.Column<long>(type: "bigint", nullable: false),
                    link_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    telegram_chat_id = table.Column<long>(type: "bigint", nullable: true),
                    linked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscribers", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sent_picks_profile_id_product_id",
                schema: "picks",
                table: "sent_picks",
                columns: new[] { "profile_id", "product_id" });

            migrationBuilder.CreateIndex(
                name: "ix_subscribers_link_code",
                schema: "picks",
                table: "subscribers",
                column: "link_code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_subscribers_profile_id",
                schema: "picks",
                table: "subscribers",
                column: "profile_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sent_picks",
                schema: "picks");

            migrationBuilder.DropTable(
                name: "subscribers",
                schema: "picks");
        }
    }
}
