using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Lst.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSlice1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "platform");

            migrationBuilder.EnsureSchema(
                name: "operations");

            migrationBuilder.EnsureSchema(
                name: "shipments");

            migrationBuilder.EnsureSchema(
                name: "identity");

            migrationBuilder.CreateTable(
                name: "audit_entries",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    actor_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    entity_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    entity_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    reason_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    changes = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_entries", x => x.id);
                    table.CheckConstraint("ck_audit_entries_actor_type", "actor_type IN ('User','Carrier','System','Anonymous')");
                    table.CheckConstraint("ck_audit_entries_outcome", "outcome IN ('Succeeded','Rejected')");
                });

            migrationBuilder.CreateTable(
                name: "hubs",
                schema: "operations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    city = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    district = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_hubs", x => x.id);
                    table.CheckConstraint("ck_hubs_code_format", "code ~ '^[A-Z]{3}-[0-9]{2}$'");
                });

            migrationBuilder.CreateTable(
                name: "idempotency_records",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_scope = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    endpoint = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    request_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    request_hash_version = table.Column<short>(type: "smallint", nullable: false),
                    response_status_code = table.Column<short>(type: "smallint", nullable: true),
                    response_content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    response_body = table.Column<byte[]>(type: "bytea", nullable: true),
                    response_location = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    response_etag = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_records", x => x.id);
                    table.CheckConstraint("ck_idempotency_records_completion", "(completed_at IS NULL AND response_status_code IS NULL AND response_body IS NULL AND response_content_type IS NULL) OR (completed_at IS NOT NULL AND response_status_code BETWEEN 200 AND 299 AND response_body IS NOT NULL AND response_content_type IS NOT NULL)");
                    table.CheckConstraint("ck_idempotency_records_expiry", "expires_at > created_at");
                    table.CheckConstraint("ck_idempotency_records_hash_length", "octet_length(request_hash) = 32");
                    table.CheckConstraint("ck_idempotency_records_hash_version", "request_hash_version >= 1");
                    table.CheckConstraint("ck_idempotency_records_key_format", "idempotency_key ~ '^[A-Za-z0-9_-]{16,128}$'");
                });

            migrationBuilder.CreateTable(
                name: "roles",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    normalized_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    concurrency_stamp = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_roles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    user_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    normalized_user_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    normalized_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    email_confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    password_hash = table.Column<string>(type: "text", nullable: true),
                    security_stamp = table.Column<string>(type: "text", nullable: true),
                    concurrency_stamp = table.Column<string>(type: "text", nullable: true),
                    phone_number = table.Column<string>(type: "text", nullable: true),
                    phone_number_confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    two_factor_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    lockout_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lockout_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    access_failed_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "shipments",
                schema: "shipments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tracking_number = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    public_tracking_code = table.Column<string>(type: "character varying(26)", maxLength: 26, nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "Forward"),
                    external_reference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    service_level = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status_occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    origin_hub_id = table.Column<Guid>(type: "uuid", nullable: false),
                    destination_hub_id = table.Column<Guid>(type: "uuid", nullable: false),
                    current_hub_id = table.Column<Guid>(type: "uuid", nullable: true),
                    package_count = table.Column<short>(type: "smallint", nullable: false),
                    promised_delivery_date = table.Column<DateOnly>(type: "date", nullable: false),
                    delivery_attempt_count = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    declared_value_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    declared_value_currency = table.Column<string>(type: "character(3)", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shipments", x => x.id);
                    table.CheckConstraint("ck_shipments_at_hub_requires_hub", "status <> 'AtHub' OR current_hub_id IS NOT NULL");
                    table.CheckConstraint("ck_shipments_declared_value_amount", "declared_value_amount IS NULL OR declared_value_amount >= 0");
                    table.CheckConstraint("ck_shipments_declared_value_currency", "declared_value_currency IS NULL OR declared_value_currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_shipments_declared_value_pair", "(declared_value_amount IS NULL) = (declared_value_currency IS NULL)");
                    table.CheckConstraint("ck_shipments_delivery_attempt_count", "delivery_attempt_count >= 0");
                    table.CheckConstraint("ck_shipments_kind", "kind IN ('Forward','Return')");
                    table.CheckConstraint("ck_shipments_package_count", "package_count BETWEEN 1 AND 20");
                    table.CheckConstraint("ck_shipments_public_tracking_code_format", "public_tracking_code ~ '^[0-9A-HJKMNP-TV-Z]{26}$'");
                    table.CheckConstraint("ck_shipments_service_level", "service_level IN ('Standard','Express')");
                    table.CheckConstraint("ck_shipments_status", "status IN ('Created','AtHub','InTransit','OutForDelivery','DeliveryFailed','Delivered','ReturningToSender','ReturnedToSender','Cancelled')");
                    table.CheckConstraint("ck_shipments_tracking_number_format", "tracking_number ~ '^LS[0-9]{10}$'");
                    table.ForeignKey(
                        name: "fk_shipments_hubs_current_hub_id",
                        column: x => x.current_hub_id,
                        principalSchema: "operations",
                        principalTable: "hubs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_shipments_hubs_destination_hub_id",
                        column: x => x.destination_hub_id,
                        principalSchema: "operations",
                        principalTable: "hubs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_shipments_hubs_origin_hub_id",
                        column: x => x.origin_hub_id,
                        principalSchema: "operations",
                        principalTable: "hubs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "role_claims",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_type = table.Column<string>(type: "text", nullable: true),
                    claim_value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_role_claims", x => x.id);
                    table.ForeignKey(
                        name: "fk_role_claims_roles_role_id",
                        column: x => x.role_id,
                        principalSchema: "identity",
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_claims",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_type = table.Column<string>(type: "text", nullable: true),
                    claim_value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_claims", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_claims_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_hub_assignments",
                schema: "identity",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    hub_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    assigned_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_hub_assignments", x => new { x.user_id, x.hub_id });
                    table.ForeignKey(
                        name: "fk_user_hub_assignments_hubs_hub_id",
                        column: x => x.hub_id,
                        principalSchema: "operations",
                        principalTable: "hubs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_user_hub_assignments_users_assigned_by_user_id",
                        column: x => x.assigned_by_user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_user_hub_assignments_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_logins",
                schema: "identity",
                columns: table => new
                {
                    login_provider = table.Column<string>(type: "text", nullable: false),
                    provider_key = table.Column<string>(type: "text", nullable: false),
                    provider_display_name = table.Column<string>(type: "text", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_logins", x => new { x.login_provider, x.provider_key });
                    table.ForeignKey(
                        name: "fk_user_logins_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_roles",
                schema: "identity",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_roles", x => new { x.user_id, x.role_id });
                    table.ForeignKey(
                        name: "fk_user_roles_roles_role_id",
                        column: x => x.role_id,
                        principalSchema: "identity",
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_user_roles_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_tokens",
                schema: "identity",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    login_provider = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_tokens", x => new { x.user_id, x.login_provider, x.name });
                    table.ForeignKey(
                        name: "fk_user_tokens_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "packages",
                schema: "shipments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    shipment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<short>(type: "smallint", nullable: false),
                    label_code = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    weight_grams = table.Column<int>(type: "integer", nullable: false),
                    length_mm = table.Column<int>(type: "integer", nullable: false),
                    width_mm = table.Column<int>(type: "integer", nullable: false),
                    height_mm = table.Column<int>(type: "integer", nullable: false),
                    content_description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_packages", x => x.id);
                    table.CheckConstraint("ck_packages_dimensions", "length_mm BETWEEN 1 AND 3000 AND width_mm BETWEEN 1 AND 3000 AND height_mm BETWEEN 1 AND 3000");
                    table.CheckConstraint("ck_packages_sequence", "sequence BETWEEN 1 AND 20");
                    table.CheckConstraint("ck_packages_weight_grams", "weight_grams BETWEEN 1 AND 70000");
                    table.ForeignKey(
                        name: "fk_packages_shipments_shipment_id",
                        column: x => x.shipment_id,
                        principalSchema: "shipments",
                        principalTable: "shipments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "shipment_parties",
                schema: "shipments",
                columns: table => new
                {
                    shipment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    full_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    company_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    phone_e164 = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    phone_last4 = table.Column<string>(type: "character(4)", nullable: false),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    address_line1 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    address_line2 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    district = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    city = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    postal_code = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    country_code = table.Column<string>(type: "character(2)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shipment_parties", x => new { x.shipment_id, x.role });
                    table.CheckConstraint("ck_shipment_parties_country_code", "country_code ~ '^[A-Z]{2}$'");
                    table.CheckConstraint("ck_shipment_parties_phone_format", "phone_e164 IS NULL OR phone_e164 ~ '^\\+[1-9][0-9]{7,14}$'");
                    table.CheckConstraint("ck_shipment_parties_role", "role IN ('Sender','Recipient')");
                    table.ForeignKey(
                        name: "fk_shipment_parties_shipments_shipment_id",
                        column: x => x.shipment_id,
                        principalSchema: "shipments",
                        principalTable: "shipments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tracking_events",
                schema: "shipments",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    shipment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    is_late = table.Column<bool>(type: "boolean", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    occurred_at_offset_minutes = table.Column<short>(type: "smallint", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status_before = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    status_after = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    hub_id = table.Column<Guid>(type: "uuid", nullable: true),
                    location_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    location_city = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    package_id = table.Column<Guid>(type: "uuid", nullable: true),
                    delivery_attempt_number = table.Column<short>(type: "smallint", nullable: true),
                    reason_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    carrier_event_receipt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    carrier_sequence = table.Column<long>(type: "bigint", nullable: true),
                    client_scan_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    business_key = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    internal_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tracking_events", x => x.id);
                    table.CheckConstraint("ck_tracking_events_created_has_no_previous", "(event_type = 'Created') = (status_before IS NULL)");
                    table.CheckConstraint("ck_tracking_events_event_type", "event_type IN ('Created','ArrivedAtHub','DepartedHub','HandedToCarrier','CarrierInTransit','OutForDelivery','DeliveryFailed','Delivered','ReturnInitiated','ReturnedToSender','Cancelled')");
                    table.CheckConstraint("ck_tracking_events_outcome", "outcome IN ('Applied','Stale','PendingReview')");
                    table.CheckConstraint("ck_tracking_events_source", "source IN ('HubScan','Operator','Carrier','System')");
                    table.CheckConstraint("ck_tracking_events_status_after", "status_after IS NULL OR status_after IN ('Created','AtHub','InTransit','OutForDelivery','DeliveryFailed','Delivered','ReturningToSender','ReturnedToSender','Cancelled')");
                    table.CheckConstraint("ck_tracking_events_status_before", "status_before IS NULL OR status_before IN ('Created','AtHub','InTransit','OutForDelivery','DeliveryFailed','Delivered','ReturningToSender','ReturnedToSender','Cancelled')");
                    table.ForeignKey(
                        name: "fk_tracking_events_hubs_hub_id",
                        column: x => x.hub_id,
                        principalSchema: "operations",
                        principalTable: "hubs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_tracking_events_packages_package_id",
                        column: x => x.package_id,
                        principalSchema: "shipments",
                        principalTable: "packages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_tracking_events_shipments_shipment_id",
                        column: x => x.shipment_id,
                        principalSchema: "shipments",
                        principalTable: "shipments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_actor",
                schema: "platform",
                table: "audit_entries",
                columns: new[] { "actor_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_entity",
                schema: "platform",
                table: "audit_entries",
                columns: new[] { "entity_type", "entity_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ux_hubs_code",
                schema: "operations",
                table: "hubs",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_records_expires_at",
                schema: "platform",
                table: "idempotency_records",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ux_idempotency_records_scope_key",
                schema: "platform",
                table: "idempotency_records",
                columns: new[] { "actor_scope", "endpoint", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_packages_label_code",
                schema: "shipments",
                table: "packages",
                column: "label_code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_packages_shipment_sequence",
                schema: "shipments",
                table: "packages",
                columns: new[] { "shipment_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_role_claims_role_id",
                schema: "identity",
                table: "role_claims",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                schema: "identity",
                table: "roles",
                column: "normalized_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_shipment_parties_role_phone_last4",
                schema: "shipments",
                table: "shipment_parties",
                columns: new[] { "role", "phone_last4" });

            migrationBuilder.CreateIndex(
                name: "ix_shipments_created_at_id",
                schema: "shipments",
                table: "shipments",
                columns: new[] { "created_at", "id" },
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_shipments_current_hub_status",
                schema: "shipments",
                table: "shipments",
                columns: new[] { "current_hub_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_shipments_destination_hub_status",
                schema: "shipments",
                table: "shipments",
                columns: new[] { "destination_hub_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_shipments_external_reference",
                schema: "shipments",
                table: "shipments",
                column: "external_reference",
                filter: "external_reference IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_shipments_origin_hub_created_at",
                schema: "shipments",
                table: "shipments",
                columns: new[] { "origin_hub_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_shipments_status_promised",
                schema: "shipments",
                table: "shipments",
                columns: new[] { "status", "promised_delivery_date" });

            migrationBuilder.CreateIndex(
                name: "ux_shipments_public_tracking_code",
                schema: "shipments",
                table: "shipments",
                column: "public_tracking_code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_shipments_tracking_number",
                schema: "shipments",
                table: "shipments",
                column: "tracking_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tracking_events_business_key",
                schema: "shipments",
                table: "tracking_events",
                columns: new[] { "shipment_id", "business_key", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_tracking_events_package_id",
                schema: "shipments",
                table: "tracking_events",
                column: "package_id");

            migrationBuilder.CreateIndex(
                name: "ix_tracking_events_timeline",
                schema: "shipments",
                table: "tracking_events",
                columns: new[] { "shipment_id", "occurred_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ux_tracking_events_carrier_receipt",
                schema: "shipments",
                table: "tracking_events",
                column: "carrier_event_receipt_id",
                unique: true,
                filter: "carrier_event_receipt_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_tracking_events_hub_client_scan",
                schema: "shipments",
                table: "tracking_events",
                columns: new[] { "hub_id", "client_scan_id" },
                unique: true,
                filter: "client_scan_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_user_claims_user_id",
                schema: "identity",
                table: "user_claims",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_hub_assignments_assigned_by_user_id",
                schema: "identity",
                table: "user_hub_assignments",
                column: "assigned_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_hub_assignments_hub_id",
                schema: "identity",
                table: "user_hub_assignments",
                column: "hub_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_logins_user_id",
                schema: "identity",
                table: "user_logins",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_roles_role_id",
                schema: "identity",
                table: "user_roles",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                schema: "identity",
                table: "users",
                column: "normalized_email");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                schema: "identity",
                table: "users",
                column: "normalized_user_name",
                unique: true);

            // Karar değişikliği (Dilim 1): claim satırı yanıt alanları NULL olarak eklenir ve aynı transaction'da tamamlanır.
            // Ertelenmiş constraint trigger'ı, tamamlanmamış bir kaydın commit edilmesini (yarım "başarı") engeller.
            migrationBuilder.Sql("""
                CREATE FUNCTION platform.ensure_idempotency_record_completed() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM platform.idempotency_records r WHERE r.id = NEW.id AND r.completed_at IS NULL) THEN
                        RAISE EXCEPTION 'idempotency record % cannot be committed without a completed response', NEW.id
                            USING ERRCODE = '23514';
                    END IF;
                    RETURN NULL;
                END
                $$;

                CREATE CONSTRAINT TRIGGER trg_idempotency_records_completed_at_commit
                    AFTER INSERT OR UPDATE ON platform.idempotency_records
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION platform.ensure_idempotency_record_completed();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_idempotency_records_completed_at_commit ON platform.idempotency_records;
                DROP FUNCTION IF EXISTS platform.ensure_idempotency_record_completed();
                """);

            migrationBuilder.DropTable(
                name: "audit_entries",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "idempotency_records",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "role_claims",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "shipment_parties",
                schema: "shipments");

            migrationBuilder.DropTable(
                name: "tracking_events",
                schema: "shipments");

            migrationBuilder.DropTable(
                name: "user_claims",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "user_hub_assignments",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "user_logins",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "user_roles",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "user_tokens",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "packages",
                schema: "shipments");

            migrationBuilder.DropTable(
                name: "roles",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "users",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "shipments",
                schema: "shipments");

            migrationBuilder.DropTable(
                name: "hubs",
                schema: "operations");
        }
    }
}
