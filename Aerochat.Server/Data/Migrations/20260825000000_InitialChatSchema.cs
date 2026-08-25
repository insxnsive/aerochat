using Aerochat.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aerochat.Server.Data.Migrations;

[DbContext(typeof(ChatDb))]
[Migration("20260825000000_InitialChatSchema")]
public partial class InitialChatSchema : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "conversations",
            columns: table => new
            {
                id = table.Column<string>(type: "TEXT", nullable: false),
                kind = table.Column<string>(type: "TEXT", nullable: false),
                title = table.Column<string>(type: "TEXT", nullable: true),
                created_at = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_conversations", x => x.id);
                table.CheckConstraint("CK_conversations_kind", "kind IN ('dm','group','server_channel')");
            });

        migrationBuilder.CreateTable(
            name: "users",
            columns: table => new
            {
                id = table.Column<string>(type: "TEXT", nullable: false),
                provider = table.Column<string>(type: "TEXT", nullable: false),
                provider_user_id = table.Column<string>(type: "TEXT", nullable: false),
                display_name = table.Column<string>(type: "TEXT", nullable: false),
                email = table.Column<string>(type: "TEXT", nullable: true),
                avatar_url = table.Column<string>(type: "TEXT", nullable: true),
                created_at = table.Column<string>(type: "TEXT", nullable: false),
                updated_at = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_users", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "participants",
            columns: table => new
            {
                conversation_id = table.Column<string>(type: "TEXT", nullable: false),
                user_id = table.Column<string>(type: "TEXT", nullable: false),
                joined_at = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_participants", x => new { x.conversation_id, x.user_id });
                table.ForeignKey("FK_participants_conversations", x => x.conversation_id, "conversations", "id", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_participants_users", x => x.user_id, "users", "id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "messages",
            columns: table => new
            {
                id = table.Column<string>(type: "TEXT", nullable: false),
                conversation_id = table.Column<string>(type: "TEXT", nullable: false),
                author_id = table.Column<string>(type: "TEXT", nullable: false),
                body = table.Column<string>(type: "TEXT", nullable: false),
                kind = table.Column<string>(type: "TEXT", nullable: false),
                ref_payload_json = table.Column<string>(type: "TEXT", nullable: true),
                created_at = table.Column<string>(type: "TEXT", nullable: false),
                edited_at = table.Column<string>(type: "TEXT", nullable: true),
                deleted_at = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_messages", x => x.id);
                table.CheckConstraint("CK_messages_kind", "kind IN ('message','sticker','gif','system')");
                table.ForeignKey("FK_messages_conversations", x => x.conversation_id, "conversations", "id", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_messages_users", x => x.author_id, "users", "id", onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "ux_users_provider_provider_user_id",
            table: "users",
            columns: new[] { "provider", "provider_user_id" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_messages_conversation_created",
            table: "messages",
            columns: new[] { "conversation_id", "created_at" },
            descending: new[] { false, true });

        migrationBuilder.CreateIndex(
            name: "ix_messages_author",
            table: "messages",
            column: "author_id");

        migrationBuilder.CreateIndex(
            name: "ix_participants_user",
            table: "participants",
            column: "user_id");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ix_messages_conversation_created",
            table: "messages");
        migrationBuilder.DropIndex(
            name: "ix_messages_author",
            table: "messages");
        migrationBuilder.DropIndex(
            name: "ix_participants_user",
            table: "participants");
        migrationBuilder.DropTable(name: "messages");
        migrationBuilder.DropTable(name: "participants");
        migrationBuilder.DropIndex(
            name: "ux_users_provider_provider_user_id",
            table: "users");
        migrationBuilder.DropTable(name: "conversations");
        migrationBuilder.DropTable(name: "users");
    }
}
