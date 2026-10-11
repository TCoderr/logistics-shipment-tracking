using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lst.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Slice8PublicTrackingRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "pii_redacted_at",
                schema: "shipments",
                table: "shipments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "phone_last4",
                schema: "shipments",
                table: "shipment_parties",
                type: "character(4)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character(4)");

            migrationBuilder.AlterColumn<string>(
                name: "phone_e164",
                schema: "shipments",
                table: "shipment_parties",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16);

            migrationBuilder.AlterColumn<string>(
                name: "address_line1",
                schema: "shipments",
                table: "shipment_parties",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);

            migrationBuilder.CreateIndex(
                name: "ix_shipments_retention_candidates",
                schema: "shipments",
                table: "shipments",
                column: "status_changed_at",
                filter: "pii_redacted_at IS NULL AND status IN ('Delivered','ReturnedToSender','Cancelled')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_shipment_parties_phone_last4",
                schema: "shipments",
                table: "shipment_parties",
                sql: "(phone_e164 IS NULL) = (phone_last4 IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_shipments_retention_candidates",
                schema: "shipments",
                table: "shipments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_shipment_parties_phone_last4",
                schema: "shipments",
                table: "shipment_parties");

            migrationBuilder.DropColumn(
                name: "pii_redacted_at",
                schema: "shipments",
                table: "shipments");

            migrationBuilder.AlterColumn<string>(
                name: "phone_last4",
                schema: "shipments",
                table: "shipment_parties",
                type: "character(4)",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character(4)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "phone_e164",
                schema: "shipments",
                table: "shipment_parties",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "address_line1",
                schema: "shipments",
                table: "shipment_parties",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
                oldNullable: true);
        }
    }
}
