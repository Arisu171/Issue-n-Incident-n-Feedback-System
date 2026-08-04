using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace IncidentTracker.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SearchIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<NpgsqlTsVector>(
                name: "search_vector",
                table: "tickets",
                type: "tsvector",
                nullable: true,
                computedColumnSql: "to_tsvector('simple', coalesce(title, '') || ' ' || coalesce(body, ''))",
                stored: true);

            migrationBuilder.CreateTable(
                name: "ticket_search_comments",
                columns: table => new
                {
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    public_text = table.Column<string>(type: "text", nullable: false),
                    internal_text = table.Column<string>(type: "text", nullable: false),
                    commenters = table.Column<string>(type: "text", nullable: false),
                    mentioned = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticket_search_comments", x => x.ticket_id);
                    table.ForeignKey(
                        name: "FK_ticket_search_comments_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tickets_search_vector",
                table: "tickets",
                column: "search_vector")
                .Annotation("Npgsql:IndexMethod", "GIN");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ticket_search_comments");

            migrationBuilder.DropIndex(
                name: "ix_tickets_search_vector",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "search_vector",
                table: "tickets");
        }
    }
}
