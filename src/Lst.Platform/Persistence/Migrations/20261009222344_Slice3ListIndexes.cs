using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lst.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Slice3ListIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_shipments_promised_id",
                schema: "shipments",
                table: "shipments",
                columns: new[] { "promised_delivery_date", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_shipments_status_changed_id",
                schema: "shipments",
                table: "shipments",
                columns: new[] { "status_changed_at", "id" },
                descending: new bool[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_shipments_promised_id",
                schema: "shipments",
                table: "shipments");

            migrationBuilder.DropIndex(
                name: "ix_shipments_status_changed_id",
                schema: "shipments",
                table: "shipments");
        }
    }
}
