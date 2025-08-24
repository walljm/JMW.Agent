using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JMW.Agent.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddRegisteredAgent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RegisteredAgents",
                columns: table => new
                {
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ServiceName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    OperatingSystem = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    IsAuthorized = table.Column<bool>(type: "INTEGER", nullable: false),
                    RegisteredAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AuthorizedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AuthorizedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegisteredAgents", x => x.AgentId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RegisteredAgents");
        }
    }
}
