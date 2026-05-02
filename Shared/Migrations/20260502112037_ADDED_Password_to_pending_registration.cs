using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shared.Migrations
{
    /// <inheritdoc />
    public partial class ADDED_Password_to_pending_registration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Email",
                table: "PendingRegistrations",
                newName: "Username");

            migrationBuilder.RenameIndex(
                name: "IX_PendingRegistrations_Email",
                table: "PendingRegistrations",
                newName: "IX_PendingRegistrations_Username");

            migrationBuilder.AddColumn<string>(
                name: "PasswordBase64",
                table: "PendingRegistrations",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PasswordBase64",
                table: "PendingRegistrations");

            migrationBuilder.RenameColumn(
                name: "Username",
                table: "PendingRegistrations",
                newName: "Email");

            migrationBuilder.RenameIndex(
                name: "IX_PendingRegistrations_Username",
                table: "PendingRegistrations",
                newName: "IX_PendingRegistrations_Email");
        }
    }
}
