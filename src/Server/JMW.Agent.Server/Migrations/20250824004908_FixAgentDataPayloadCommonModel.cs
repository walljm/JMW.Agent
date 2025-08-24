using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JMW.Agent.Server.Migrations
{
    /// <inheritdoc />
    public partial class FixAgentDataPayloadCommonModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentServices");

            migrationBuilder.CreateTable(
                name: "AgentDataPayloads",
                columns: table => new
                {
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ServiceName = table.Column<string>(type: "TEXT", nullable: false),
                    InfoJson = table.Column<string>(type: "TEXT", nullable: true),
                    LastUpdated = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentDataPayloads", x => x.AgentId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentDataPayloads");

            migrationBuilder.CreateTable(
                name: "AgentServices",
                columns: table => new
                {
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    InfoJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentServices", x => x.Name);
                });
        }
    }
}
