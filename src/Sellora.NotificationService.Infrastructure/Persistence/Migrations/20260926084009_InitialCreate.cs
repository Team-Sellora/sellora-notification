using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.NotificationService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "notification_request",
                columns: table => new
                {
                    notification_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    template_key = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_reference = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source_topic = table.Column<string>(type: "character varying(249)", maxLength: 249, nullable: false),
                    source_partition = table.Column<int>(type: "integer", nullable: false),
                    source_offset = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_request", x => x.notification_request_id);
                    table.CheckConstraint("ck_notification_request_status", "status IN ('Pending')");
                });

            migrationBuilder.CreateTable(
                name: "notification_recipient",
                columns: table => new
                {
                    notification_recipient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    notification_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    recipient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_recipient", x => x.notification_recipient_id);
                    table.CheckConstraint("ck_notification_recipient_kind", "kind IN ('Shop', 'Agency')");
                    table.ForeignKey(
                        name: "fk_notification_recipient_request",
                        column: x => x.notification_request_id,
                        principalTable: "notification_request",
                        principalColumn: "notification_request_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "uq_notification_recipient_request_kind",
                table: "notification_recipient",
                columns: new[] { "notification_request_id", "kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_notification_request_company_order",
                table: "notification_request",
                columns: new[] { "company_id", "order_id" });

            migrationBuilder.CreateIndex(
                name: "ix_notification_request_status_received",
                table: "notification_request",
                columns: new[] { "status", "received_at" });

            migrationBuilder.CreateIndex(
                name: "uq_notification_request_source_event",
                table: "notification_request",
                column: "source_event_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notification_recipient");

            migrationBuilder.DropTable(
                name: "notification_request");
        }
    }
}
