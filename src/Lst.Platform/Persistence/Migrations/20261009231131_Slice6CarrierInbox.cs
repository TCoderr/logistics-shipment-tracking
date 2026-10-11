using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lst.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Slice6CarrierInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "tracking");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "carrier_handed_over_at",
                schema: "shipments",
                table: "shipments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "carrier_id",
                schema: "shipments",
                table: "shipments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "last_carrier_sequence",
                schema: "shipments",
                table: "shipments",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "carriers",
                schema: "tracking",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    sends_sequence = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_carriers", x => x.id);
                    table.CheckConstraint("ck_carriers_code_format", "code ~ '^[A-Z0-9]{2,16}$'");
                });

            migrationBuilder.CreateTable(
                name: "carrier_event_receipts",
                schema: "tracking",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    carrier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_event_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    payload_sha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    raw_sha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    raw_body = table.Column<byte[]>(type: "bytea", nullable: true),
                    payload = table.Column<string>(type: "jsonb", nullable: true),
                    payload_purged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    signature_timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tracking_number = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: true),
                    shipment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    event_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    carrier_sequence = table.Column<long>(type: "bigint", nullable: true),
                    processing_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    outcome_reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    attempts = table.Column<short>(type: "smallint", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lock_token = table.Column<Guid>(type: "uuid", nullable: true),
                    locked_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_carrier_event_receipts", x => x.id);
                    table.CheckConstraint("ck_receipts_attempts", "attempts >= 0");
                    table.CheckConstraint("ck_receipts_outcome", "outcome IS NULL OR outcome IN ('Applied','Duplicate','Stale','Rejected','PendingReview')");
                    table.CheckConstraint("ck_receipts_payload_sha256", "octet_length(payload_sha256) = 32");
                    table.CheckConstraint("ck_receipts_processed_has_outcome", "(processing_status = 'Processed') = (outcome IS NOT NULL)");
                    table.CheckConstraint("ck_receipts_processing_has_lease", "processing_status <> 'Processing' OR (lock_token IS NOT NULL AND locked_until IS NOT NULL)");
                    table.CheckConstraint("ck_receipts_raw_or_purged", "raw_body IS NOT NULL OR payload_purged_at IS NOT NULL");
                    table.CheckConstraint("ck_receipts_raw_sha256", "octet_length(raw_sha256) = 32");
                    table.CheckConstraint("ck_receipts_sequence", "carrier_sequence IS NULL OR carrier_sequence >= 0");
                    table.CheckConstraint("ck_receipts_status", "processing_status IN ('Received','Processing','Processed','DeadLettered')");
                    table.ForeignKey(
                        name: "fk_carrier_event_receipts_carriers_carrier_id",
                        column: x => x.carrier_id,
                        principalSchema: "tracking",
                        principalTable: "carriers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_carrier_event_receipts_shipments_shipment_id",
                        column: x => x.shipment_id,
                        principalSchema: "shipments",
                        principalTable: "shipments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "carrier_event_conflicts",
                schema: "tracking",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    payload_sha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    raw_sha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    raw_body = table.Column<byte[]>(type: "bytea", nullable: true),
                    payload_purged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    signature_timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_carrier_event_conflicts", x => x.id);
                    table.CheckConstraint("ck_conflicts_payload_sha256", "octet_length(payload_sha256) = 32");
                    table.CheckConstraint("ck_conflicts_raw_or_purged", "raw_body IS NOT NULL OR payload_purged_at IS NOT NULL");
                    table.ForeignKey(
                        name: "fk_carrier_event_conflicts_carrier_event_receipts_receipt_id",
                        column: x => x.receipt_id,
                        principalSchema: "tracking",
                        principalTable: "carrier_event_receipts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                schema: "tracking",
                table: "carriers",
                columns: new[] { "id", "code", "is_active", "name", "sends_sequence" },
                values: new object[] { new Guid("0192a6c0-5100-7000-8000-00000051e7e0"), "SIMEX", true, "SimEx Kargo (simülasyon)", true });

            migrationBuilder.CreateIndex(
                name: "ix_shipments_carrier_status",
                schema: "shipments",
                table: "shipments",
                columns: new[] { "carrier_id", "status" },
                filter: "carrier_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_carrier_event_conflicts_receipt",
                schema: "tracking",
                table: "carrier_event_conflicts",
                column: "receipt_id");

            migrationBuilder.CreateIndex(
                name: "ix_receipts_pending",
                schema: "tracking",
                table: "carrier_event_receipts",
                column: "next_attempt_at",
                filter: "processing_status IN ('Received','Processing')");

            migrationBuilder.CreateIndex(
                name: "ix_receipts_shipment",
                schema: "tracking",
                table: "carrier_event_receipts",
                columns: new[] { "shipment_id", "received_at" });

            migrationBuilder.CreateIndex(
                name: "ix_receipts_tracking_number",
                schema: "tracking",
                table: "carrier_event_receipts",
                columns: new[] { "tracking_number", "received_at" });

            migrationBuilder.CreateIndex(
                name: "ux_carrier_event_receipts_carrier_event",
                schema: "tracking",
                table: "carrier_event_receipts",
                columns: new[] { "carrier_id", "external_event_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_carriers_code",
                schema: "tracking",
                table: "carriers",
                column: "code",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_shipments_carriers_carrier_id",
                schema: "shipments",
                table: "shipments",
                column: "carrier_id",
                principalSchema: "tracking",
                principalTable: "carriers",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_tracking_events_carrier_event_receipts_carrier_event_receip",
                schema: "shipments",
                table: "tracking_events",
                column: "carrier_event_receipt_id",
                principalSchema: "tracking",
                principalTable: "carrier_event_receipts",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_shipments_carriers_carrier_id",
                schema: "shipments",
                table: "shipments");

            migrationBuilder.DropForeignKey(
                name: "fk_tracking_events_carrier_event_receipts_carrier_event_receip",
                schema: "shipments",
                table: "tracking_events");

            migrationBuilder.DropTable(
                name: "carrier_event_conflicts",
                schema: "tracking");

            migrationBuilder.DropTable(
                name: "carrier_event_receipts",
                schema: "tracking");

            migrationBuilder.DropTable(
                name: "carriers",
                schema: "tracking");

            migrationBuilder.DropIndex(
                name: "ix_shipments_carrier_status",
                schema: "shipments",
                table: "shipments");

            migrationBuilder.DropColumn(
                name: "carrier_handed_over_at",
                schema: "shipments",
                table: "shipments");

            migrationBuilder.DropColumn(
                name: "carrier_id",
                schema: "shipments",
                table: "shipments");

            migrationBuilder.DropColumn(
                name: "last_carrier_sequence",
                schema: "shipments",
                table: "shipments");
        }
    }
}
