using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JMW.Agent.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddMachineInfoColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InfoJson",
                table: "AgentServices",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InfoJson",
                table: "AgentServices");
        }
    }
}
