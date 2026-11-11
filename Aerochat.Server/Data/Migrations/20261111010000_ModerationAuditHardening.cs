using Aerochat.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Aerochat.Server.Data.Migrations;

[DbContext(typeof(ChatDb))]
[Migration("20261111010000_ModerationAuditHardening")]
public sealed class ModerationAuditHardening : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TRIGGER moderation_audit_no_replace BEFORE INSERT ON moderation_audit
                WHEN EXISTS (SELECT 1 FROM moderation_audit WHERE Id = NEW.Id)
                BEGIN SELECT RAISE(ABORT, 'Audit records are append-only.'); END;
            CREATE TRIGGER moderation_audit_validate_insert BEFORE INSERT ON moderation_audit
                WHEN NEW.Action NOT IN ('message.delete', 'member.kick', 'user.ban', 'user.unban')
                  OR (NEW.ReasonCode IS NOT NULL AND NEW.ReasonCode NOT IN ('spam', 'abuse', 'policy'))
                BEGIN SELECT RAISE(ABORT, 'Invalid audit metadata.'); END;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TRIGGER moderation_audit_validate_insert;
            DROP TRIGGER moderation_audit_no_replace;
            """);
    }
}
