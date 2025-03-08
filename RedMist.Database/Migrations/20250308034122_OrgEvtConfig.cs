using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RedMist.Migrations
{
    /// <inheritdoc />
    public partial class OrgEvtConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ControlLogParameter",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "ControlLogType",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "EventReferenceId",
                table: "Events");

            migrationBuilder.AlterColumn<string>(
                name: "Website",
                table: "Organizations",
                type: "nvarchar(1024)",
                maxLength: 1024,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(512)",
                oldMaxLength: 512,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ControlLogParams",
                table: "Organizations",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ControlLogType",
                table: "Organizations",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Orbits",
                table: "Organizations",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "X2",
                table: "Organizations",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Broadcast",
                table: "Events",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CourseConfiguration",
                table: "Events",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Distance",
                table: "Events",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "EndDate",
                table: "Events",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "ScheduleJson",
                table: "Events",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TrackName",
                table: "Events",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_Organizations_ClientId",
                table: "Organizations",
                column: "ClientId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Organizations_ClientId",
                table: "Organizations");

            migrationBuilder.DropColumn(
                name: "ControlLogParams",
                table: "Organizations");

            migrationBuilder.DropColumn(
                name: "ControlLogType",
                table: "Organizations");

            migrationBuilder.DropColumn(
                name: "Orbits",
                table: "Organizations");

            migrationBuilder.DropColumn(
                name: "X2",
                table: "Organizations");

            migrationBuilder.DropColumn(
                name: "Broadcast",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "CourseConfiguration",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "Distance",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "EndDate",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "ScheduleJson",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "TrackName",
                table: "Events");

            migrationBuilder.AlterColumn<string>(
                name: "Website",
                table: "Organizations",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(1024)",
                oldMaxLength: 1024,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ControlLogParameter",
                table: "Events",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ControlLogType",
                table: "Events",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "EventReferenceId",
                table: "Events",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }
    }
}
