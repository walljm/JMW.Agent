using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JMW.Agent.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddProperAgentDataPayloadForeignKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddForeignKey(
                name: "FK_AgentDataPayloads_RegisteredAgents_AgentId",
                table: "AgentDataPayloads",
                column: "AgentId",
                principalTable: "RegisteredAgents",
                principalColumn: "AgentId",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AgentDataPayloads_RegisteredAgents_AgentId",
                table: "AgentDataPayloads");
        }
    }
}
