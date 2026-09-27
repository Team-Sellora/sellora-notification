using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.NotificationService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDispatchState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_notification_request_status",
                table: "notification_request");

            migrationBuilder.AddColumn<int>(
                name: "attempt_count",
                table: "notification_request",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "claimed_until",
                table: "notification_request",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "completed_at",
                table: "notification_request",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_attempt_at",
                table: "notification_request",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "next_attempt_at",
                table: "notification_request",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "rendered_at",
                table: "notification_request",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "rendered_body_sha256",
                table: "notification_request",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "rendered_html",
                table: "notification_request",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "rendered_subject",
                table: "notification_request",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "rendered_text",
                table: "notification_request",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "send_gap_ms",
                table: "notification_request",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "attempts",
                table: "notification_recipient",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "delivery_status",
                table: "notification_recipient",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Pending");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_attempt_at",
                table: "notification_recipient",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_error",
                table: "notification_recipient",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "provider_message_id",
                table: "notification_recipient",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "sent_at",
                table: "notification_recipient",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_notification_request_dispatch_due",
                table: "notification_request",
                columns: new[] { "status", "next_attempt_at" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_notification_request_status",
                table: "notification_request",
                sql: "status IN ('Pending', 'Sent', 'PartiallySent')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_notification_recipient_delivery_status",
                table: "notification_recipient",
                sql: "delivery_status IN ('Pending', 'Sent', 'Failed', 'Unaddressed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_notification_request_dispatch_due",
                table: "notification_request");

            migrationBuilder.DropCheckConstraint(
                name: "ck_notification_request_status",
                table: "notification_request");

            migrationBuilder.DropCheckConstraint(
                name: "ck_notification_recipient_delivery_status",
                table: "notification_recipient");

            migrationBuilder.DropColumn(
                name: "attempt_count",
                table: "notification_request");

            migrationBuilder.DropColumn(
                name: "claimed_until",
                table: "notification_request");

            migrationBuilder.DropColumn(
                name: "completed_at",
                table: "notification_request");

            migrationBuilder.DropColumn(
                name: "last_attempt_at",
                table: "notification_request");

            migrationBuilder.DropColumn(
                name: "next_attempt_at",
                table: "notification_request");

            migrationBuilder.DropColumn(
                name: "rendered_at",
                table: "notification_request");

            migrationBuilder.DropColumn(
                name: "rendered_body_sha256",
                table: "notification_request");

            migrationBuilder.DropColumn(
                name: "rendered_html",
                table: "notification_request");

            migrationBuilder.DropColumn(
                name: "rendered_subject",
                table: "notification_request");

            migrationBuilder.DropColumn(
                name: "rendered_text",
                table: "notification_request");

            migrationBuilder.DropColumn(
                name: "send_gap_ms",
                table: "notification_request");

            migrationBuilder.DropColumn(
                name: "attempts",
                table: "notification_recipient");

            migrationBuilder.DropColumn(
                name: "delivery_status",
                table: "notification_recipient");

            migrationBuilder.DropColumn(
                name: "last_attempt_at",
                table: "notification_recipient");

            migrationBuilder.DropColumn(
                name: "last_error",
                table: "notification_recipient");

            migrationBuilder.DropColumn(
                name: "provider_message_id",
                table: "notification_recipient");

            migrationBuilder.DropColumn(
                name: "sent_at",
                table: "notification_recipient");

            migrationBuilder.AddCheckConstraint(
                name: "ck_notification_request_status",
                table: "notification_request",
                sql: "status IN ('Pending')");
        }
    }
}
