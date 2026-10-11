using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lst.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Slice4ScansAndCancel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "cancellation_reason",
                schema: "shipments",
                table: "shipments",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "extra_attempts_granted",
                schema: "shipments",
                table: "shipments",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<bool>(
                name: "has_departed",
                schema: "shipments",
                table: "shipments",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "current_hub_id",
                schema: "shipments",
                table: "packages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_scan_type",
                schema: "shipments",
                table: "packages",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_scanned_at",
                schema: "shipments",
                table: "packages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_shipments_cancelled_requires_reason",
                schema: "shipments",
                table: "shipments",
                sql: "status <> 'Cancelled' OR cancellation_reason IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_shipments_extra_attempts",
                schema: "shipments",
                table: "shipments",
                sql: "extra_attempts_granted >= 0");

            migrationBuilder.CreateIndex(
                name: "ix_packages_current_hub_id",
                schema: "shipments",
                table: "packages",
                column: "current_hub_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_packages_last_scan_type",
                schema: "shipments",
                table: "packages",
                sql: "last_scan_type IS NULL OR last_scan_type IN ('Arrival','Departure','OutForDelivery')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_packages_scan_pair",
                schema: "shipments",
                table: "packages",
                sql: "(last_scan_type IS NULL) = (last_scanned_at IS NULL)");

            migrationBuilder.AddForeignKey(
                name: "fk_packages_hubs_current_hub_id",
                schema: "shipments",
                table: "packages",
                column: "current_hub_id",
                principalSchema: "operations",
                principalTable: "hubs",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_packages_hubs_current_hub_id",
                schema: "shipments",
                table: "packages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_shipments_cancelled_requires_reason",
                schema: "shipments",
                table: "shipments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_shipments_extra_attempts",
                schema: "shipments",
                table: "shipments");

            migrationBuilder.DropIndex(
                name: "ix_packages_current_hub_id",
                schema: "shipments",
                table: "packages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_packages_last_scan_type",
                schema: "shipments",
                table: "packages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_packages_scan_pair",
                schema: "shipments",
                table: "packages");

            migrationBuilder.DropColumn(
                name: "cancellation_reason",
                schema: "shipments",
                table: "shipments");

            migrationBuilder.DropColumn(
                name: "extra_attempts_granted",
                schema: "shipments",
                table: "shipments");

            migrationBuilder.DropColumn(
                name: "has_departed",
                schema: "shipments",
                table: "shipments");

            migrationBuilder.DropColumn(
                name: "current_hub_id",
                schema: "shipments",
                table: "packages");

            migrationBuilder.DropColumn(
                name: "last_scan_type",
                schema: "shipments",
                table: "packages");

            migrationBuilder.DropColumn(
                name: "last_scanned_at",
                schema: "shipments",
                table: "packages");
        }
    }
}
