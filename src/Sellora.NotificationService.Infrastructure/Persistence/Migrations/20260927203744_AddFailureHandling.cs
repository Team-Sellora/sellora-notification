using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.NotificationService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFailureHandling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_notification_request_status",
                table: "notification_request");

            migrationBuilder.DropCheckConstraint(
                name: "ck_notification_recipient_delivery_status",
                table: "notification_recipient");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_resend_at",
                table: "notification_request",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_resend_by",
                table: "notification_request",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "failures_since_reset",
                table: "notification_recipient",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "notification_attempt",
                columns: table => new
                {
                    notification_attempt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    notification_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    notification_recipient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipient_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    email_address = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    attempt_number = table.Column<int>(type: "integer", nullable: false),
                    attempted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    outcome = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    provider_response = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    attempt_trigger = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    triggered_by = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_attempt", x => x.notification_attempt_id);
                    table.CheckConstraint("ck_notification_attempt_outcome", "outcome IN ('Sent', 'TransientFailure', 'PermanentFailure')");
                    table.CheckConstraint("ck_notification_attempt_trigger", "attempt_trigger IN ('Automatic', 'ManualResend')");
                    table.ForeignKey(
                        name: "fk_notification_attempt_request",
                        column: x => x.notification_request_id,
                        principalTable: "notification_request",
                        principalColumn: "notification_request_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_notification_request_status",
                table: "notification_request",
                sql: "status IN ('Pending', 'Sent', 'PartiallySent', 'Failed', 'PermanentlyFailed')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_notification_recipient_delivery_status",
                table: "notification_recipient",
                sql: "delivery_status IN ('Pending', 'Sent', 'Failed', 'Unaddressed', 'PermanentlyFailed')");

            migrationBuilder.CreateIndex(
                name: "ix_notification_attempt_request_time",
                table: "notification_attempt",
                columns: new[] { "notification_request_id", "attempted_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notification_attempt");

            migrationBuilder.DropCheckConstraint(
                name: "ck_notification_request_status",
                table: "notification_request");

            migrationBuilder.DropCheckConstraint(
                name: "ck_notification_recipient_delivery_status",
                table: "notification_recipient");

            migrationBuilder.DropColumn(
                name: "last_resend_at",
                table: "notification_request");

            migrationBuilder.DropColumn(
                name: "last_resend_by",
                table: "notification_request");

            migrationBuilder.DropColumn(
                name: "failures_since_reset",
                table: "notification_recipient");

            migrationBuilder.AddCheckConstraint(
                name: "ck_notification_request_status",
                table: "notification_request",
                sql: "status IN ('Pending', 'Sent', 'PartiallySent')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_notification_recipient_delivery_status",
                table: "notification_recipient",
                sql: "delivery_status IN ('Pending', 'Sent', 'Failed', 'Unaddressed')");
        }
    }
}
