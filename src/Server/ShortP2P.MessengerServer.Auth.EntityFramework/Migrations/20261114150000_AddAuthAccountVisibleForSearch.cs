using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShortP2P.MessengerServer.Auth.EntityFramework.Migrations;

[DbContext(typeof(AuthDbContext))]
[Migration("20261114150000_AddAuthAccountVisibleForSearch")]
public partial class AddAuthAccountVisibleForSearch : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "VisibleForSearch",
            table: "auth_accounts",
            type: "INTEGER",
            nullable: false,
            defaultValue: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "VisibleForSearch",
            table: "auth_accounts");
    }
}
