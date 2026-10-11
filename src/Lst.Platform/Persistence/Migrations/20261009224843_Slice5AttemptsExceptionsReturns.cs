using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lst.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Slice5AttemptsExceptionsReturns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "delivered_at",
                schema: "shipments",
                table: "shipments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "delivery_attempts",
                schema: "shipments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    shipment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_number = table.Column<short>(type: "smallint", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    failure_reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    tracking_event_id = table.Column<long>(type: "bigint", nullable: false),
                    recorded_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_delivery_attempts", x => x.id);
                    table.CheckConstraint("ck_delivery_attempts_failed_has_reason", "(outcome = 'Failed') = (failure_reason IS NOT NULL)");
                    table.CheckConstraint("ck_delivery_attempts_number", "attempt_number >= 1");
                    table.CheckConstraint("ck_delivery_attempts_other_has_note", "failure_reason IS DISTINCT FROM 'Other' OR note IS NOT NULL");
                    table.CheckConstraint("ck_delivery_attempts_outcome", "outcome IN ('Delivered','Failed')");
                    table.CheckConstraint("ck_delivery_attempts_reason", "failure_reason IS NULL OR failure_reason IN ('RecipientAbsent','AddressNotFound','RecipientRefused','AccessRestricted','PackageMissing','PackageDamaged','Other')");
                    table.ForeignKey(
                        name: "fk_delivery_attempts_shipments_shipment_id",
                        column: x => x.shipment_id,
                        principalSchema: "shipments",
                        principalTable: "shipments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_delivery_attempts_tracking_events_tracking_event_id",
                        column: x => x.tracking_event_id,
                        principalSchema: "shipments",
                        principalTable: "tracking_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "shipment_exceptions",
                schema: "operations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    shipment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    severity = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    hub_id = table.Column<Guid>(type: "uuid", nullable: true),
                    occurrence_count = table.Column<int>(type: "integer", nullable: false),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    opened_by_source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    assigned_to_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resolution_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    resolution_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shipment_exceptions", x => x.id);
                    table.CheckConstraint("ck_shipment_exceptions_assigned_has_user", "status <> 'Assigned' OR assigned_to_user_id IS NOT NULL");
                    table.CheckConstraint("ck_shipment_exceptions_occurrence", "occurrence_count >= 1");
                    table.CheckConstraint("ck_shipment_exceptions_resolution_code", "resolution_code IS NULL OR resolution_code IN ('ReattemptApproved','ReturnToSenderInitiated','CarrierConfirmed','EventAppliedManually','EventDiscarded','PackagesConsolidated','NoActionNeeded','AutoResolved')");
                    table.CheckConstraint("ck_shipment_exceptions_resolved_fields", "(status = 'Resolved') = (resolved_at IS NOT NULL AND resolution_code IS NOT NULL)");
                    table.CheckConstraint("ck_shipment_exceptions_severity", "severity IN ('Low','Medium','High')");
                    table.CheckConstraint("ck_shipment_exceptions_status", "status IN ('Open','Assigned','Resolved')");
                    table.CheckConstraint("ck_shipment_exceptions_type", "type IN ('DeliveryFailed','MaxAttemptsReached','DeliveryOverdue','NoMovement','PackageSplit','CarrierEventPendingReview','CarrierEventConflict')");
                    table.ForeignKey(
                        name: "fk_shipment_exceptions_hubs_hub_id",
                        column: x => x.hub_id,
                        principalSchema: "operations",
                        principalTable: "hubs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_shipment_exceptions_shipments_shipment_id",
                        column: x => x.shipment_id,
                        principalSchema: "shipments",
                        principalTable: "shipments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_shipment_exceptions_users_assigned_to_user_id",
                        column: x => x.assigned_to_user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_shipment_exceptions_users_resolved_by_user_id",
                        column: x => x.resolved_by_user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_shipments_delivered_at",
                schema: "shipments",
                table: "shipments",
                column: "delivered_at",
                filter: "delivered_at IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_shipments_delivered_at",
                schema: "shipments",
                table: "shipments",
                sql: "(status = 'Delivered') = (delivered_at IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "ux_delivery_attempts_shipment_number",
                schema: "shipments",
                table: "delivery_attempts",
                columns: new[] { "shipment_id", "attempt_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_delivery_attempts_tracking_event",
                schema: "shipments",
                table: "delivery_attempts",
                column: "tracking_event_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_shipment_exceptions_assignee",
                schema: "operations",
                table: "shipment_exceptions",
                columns: new[] { "assigned_to_user_id", "status" },
                filter: "status = 'Assigned'");

            migrationBuilder.CreateIndex(
                name: "ix_shipment_exceptions_hub_status",
                schema: "operations",
                table: "shipment_exceptions",
                columns: new[] { "hub_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_shipment_exceptions_opened",
                schema: "operations",
                table: "shipment_exceptions",
                columns: new[] { "opened_at", "id" },
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_shipment_exceptions_queue_due",
                schema: "operations",
                table: "shipment_exceptions",
                columns: new[] { "due_at", "id" },
                filter: "status <> 'Resolved'");

            migrationBuilder.CreateIndex(
                name: "ix_shipment_exceptions_resolved_by_user_id",
                schema: "operations",
                table: "shipment_exceptions",
                column: "resolved_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_shipment_exceptions_open_per_type",
                schema: "operations",
                table: "shipment_exceptions",
                columns: new[] { "shipment_id", "type" },
                unique: true,
                filter: "status IN ('Open','Assigned')");

            // Operations modülünün okuma sözleşmesi: Shipments yalnız bu view'ı okur (ADR-015).
            migrationBuilder.Sql("""
                CREATE VIEW operations.v_open_shipment_exceptions AS
                SELECT id, shipment_id, type, severity, status, hub_id, occurrence_count, opened_at, due_at, assigned_to_user_id
                  FROM operations.shipment_exceptions
                 WHERE status IN ('Open', 'Assigned');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW IF EXISTS operations.v_open_shipment_exceptions;");
            migrationBuilder.DropTable(
                name: "delivery_attempts",
                schema: "shipments");

            migrationBuilder.DropTable(
                name: "shipment_exceptions",
                schema: "operations");

            migrationBuilder.DropIndex(
                name: "ix_shipments_delivered_at",
                schema: "shipments",
                table: "shipments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_shipments_delivered_at",
                schema: "shipments",
                table: "shipments");

            migrationBuilder.DropColumn(
                name: "delivered_at",
                schema: "shipments",
                table: "shipments");
        }
    }
}
