using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartStylist.Modules.Catalog.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ShopPlatformAndColour : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "accepts_foreign_card",
                schema: "catalog",
                table: "shops",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "accepts_forwarder",
                schema: "catalog",
                table: "shops",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "checkout_note",
                schema: "catalog",
                table: "shops",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "platform",
                schema: "catalog",
                table: "shops",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "platform_checked_at",
                schema: "catalog",
                table: "shops",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "platform_evidence",
                schema: "catalog",
                table: "shops",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "colour",
                schema: "catalog",
                table: "price_snapshots",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "accepts_foreign_card",
                schema: "catalog",
                table: "shops");

            migrationBuilder.DropColumn(
                name: "accepts_forwarder",
                schema: "catalog",
                table: "shops");

            migrationBuilder.DropColumn(
                name: "checkout_note",
                schema: "catalog",
                table: "shops");

            migrationBuilder.DropColumn(
                name: "platform",
                schema: "catalog",
                table: "shops");

            migrationBuilder.DropColumn(
                name: "platform_checked_at",
                schema: "catalog",
                table: "shops");

            migrationBuilder.DropColumn(
                name: "platform_evidence",
                schema: "catalog",
                table: "shops");

            migrationBuilder.DropColumn(
                name: "colour",
                schema: "catalog",
                table: "price_snapshots");
        }
    }
}
