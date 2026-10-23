using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sellora.NotificationService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDeliveryAndLowStock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_notification_recipient_kind",
                table: "notification_recipient");

            migrationBuilder.AlterColumn<Guid>(
                name: "order_id",
                table: "notification_request",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "context",
                table: "notification_request",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "notification_directory",
                columns: table => new
                {
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_directory", x => new { x.company_id, x.kind, x.entry_id });
                    table.CheckConstraint("ck_notification_directory_kind", "kind IN ('Agency', 'Shop', 'Product')");
                });

            migrationBuilder.CreateTable(
                name: "notification_settings",
                columns: table => new
                {
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    alert_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    updated_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_settings", x => x.company_id);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_notification_recipient_kind",
                table: "notification_recipient",
                sql: "kind IN ('Shop', 'Agency', 'CompanyAdmin')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notification_directory");

            migrationBuilder.DropTable(
                name: "notification_settings");

            migrationBuilder.DropCheckConstraint(
                name: "ck_notification_recipient_kind",
                table: "notification_recipient");

            migrationBuilder.DropColumn(
                name: "context",
                table: "notification_request");

            migrationBuilder.AlterColumn<Guid>(
                name: "order_id",
                table: "notification_request",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_notification_recipient_kind",
                table: "notification_recipient",
                sql: "kind IN ('Shop', 'Agency')");
        }
    }
}
