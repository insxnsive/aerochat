using Aerochat.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Aerochat.Server.Data.Migrations;

[DbContext(typeof(ChatDb))]
[Migration("20261111000000_ModerationFoundation")]
public sealed class ModerationFoundation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE moderation_audit (
                Id TEXT NOT NULL PRIMARY KEY, ActorId TEXT NOT NULL, Action TEXT NOT NULL,
                TargetId TEXT NOT NULL, ConversationId TEXT NULL, CreatedAt TEXT NOT NULL, ReasonCode TEXT NULL);
            CREATE INDEX IX_moderation_audit_CreatedAt_Id ON moderation_audit (CreatedAt, Id);
            CREATE TABLE server_bans (UserId TEXT NOT NULL PRIMARY KEY, OperatorId TEXT NOT NULL, CreatedAt TEXT NOT NULL);
            CREATE TRIGGER moderation_audit_no_update BEFORE UPDATE ON moderation_audit
                BEGIN SELECT RAISE(ABORT, 'Audit records are append-only.'); END;
            CREATE TRIGGER moderation_audit_no_delete BEFORE DELETE ON moderation_audit
                BEGIN SELECT RAISE(ABORT, 'Audit records are append-only.'); END;
            """);
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    { migrationBuilder.DropTable("moderation_audit");migrationBuilder.DropTable("server_bans"); }
}
