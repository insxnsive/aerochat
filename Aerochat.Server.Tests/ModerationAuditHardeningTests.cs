using Aerochat.Server.Data;
using Aerochat.Server.Moderation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Aerochat.Server.Tests;

public sealed class ModerationAuditHardeningTests
{
    private const string Foundation="20261111000000_ModerationFoundation";
    private static ChatDb Db(SqliteConnection c)=>new(new DbContextOptionsBuilder<ChatDb>().UseSqlite(c).Options);
    private static void Insert(ChatDb db,string id,string? action="user.ban",string? reason=null,string verb="INSERT")
    {
        using var cmd=db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText=$"{verb} INTO moderation_audit (Id,ActorId,Action,TargetId,CreatedAt,ReasonCode) VALUES ($id,$actor,$action,$target,$time,$reason)";
        foreach(var item in new (string,object?)[]{("$id",id),("$actor","actor"),("$action",action),("$target","target"),("$time","2026-11-11T00:00:00+00:00"),("$reason",reason)})
        {var parameter=cmd.CreateParameter();parameter.ParameterName=item.Item1;parameter.Value=item.Item2??DBNull.Value;cmd.Parameters.Add(parameter);}
        cmd.ExecuteNonQuery();
    }
    private static string Row(ChatDb db,string id)
    {
        using var cmd=db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText="SELECT Id || '|' || ActorId || '|' || Action || '|' || TargetId || '|' || CreatedAt || '|' || COALESCE(ReasonCode,'NULL') FROM moderation_audit WHERE Id=$id";
        var p=cmd.CreateParameter();p.ParameterName="$id";p.Value=id;cmd.Parameters.Add(p);return (string)cmd.ExecuteScalar()!;
    }
    [TestCase("INSERT OR REPLACE")]
    [TestCase("REPLACE")]
    public void Duplicate_id_cannot_replace_with_recursive_triggers_disabled(string verb)
    {
        using var c=new SqliteConnection("Data Source=:memory:");c.Open();using var db=Db(c);db.Database.Migrate();
        db.Database.ExecuteSqlRaw("PRAGMA recursive_triggers=OFF");Insert(db,"fixture-id",reason:"policy");string before=Row(db,"fixture-id");
        var error=Assert.Throws<SqliteException>(()=>Insert(db,"fixture-id","user.unban","abuse",verb));
        Assert.That(error!.SqliteErrorCode,Is.EqualTo(19));Assert.That(error.Message,Does.Contain("Audit records are append-only."));
        Assert.That(Row(db,"fixture-id"),Is.EqualTo(before));Assert.That(db.Set<ModerationAuditRecord>().Count(),Is.EqualTo(1));
    }
    [TestCase("unknown",null)]
    [TestCase("USER.BAN",null)]
    [TestCase("",null)]
    [TestCase("user.ban","private-content")]
    [TestCase("user.ban","")]
    [TestCase("user.ban","SPAM")]
    public void Direct_sql_rejects_invalid_metadata(string action,string? reason)
    {
        using var c=new SqliteConnection("Data Source=:memory:");c.Open();using var db=Db(c);db.Database.Migrate();
        var error=Assert.Throws<SqliteException>(()=>Insert(db,"invalid-id",action,reason));
        Assert.That(error!.SqliteErrorCode,Is.EqualTo(19));Assert.That(error.Message,Does.Contain("Invalid audit metadata."));
        Assert.That(error.Message,Does.Not.Contain("private-content"));Assert.That(db.Set<ModerationAuditRecord>().Count(),Is.Zero);
    }
    [Test]
    public void Every_allowed_action_and_nullable_reason_remains_insertable()
    {
        using var c=new SqliteConnection("Data Source=:memory:");c.Open();using var db=Db(c);db.Database.Migrate();
        int count=0;
        foreach(string action in new[]{"message.delete","member.kick","user.ban","user.unban"})
        foreach(string? reason in new string?[]{null,"spam","abuse","policy"}) Insert(db,(++count).ToString(),action,reason);
        Assert.That(db.Set<ModerationAuditRecord>().Count(),Is.EqualTo(16));
    }
    [Test]
    public void Invalid_multirow_statement_is_atomic()
    {
        using var c=new SqliteConnection("Data Source=:memory:");c.Open();using var db=Db(c);db.Database.Migrate();Insert(db,"preserved");string before=Row(db,"preserved");
        Assert.Throws<SqliteException>(()=>db.Database.ExecuteSqlRaw("""
            INSERT INTO moderation_audit (Id,ActorId,Action,TargetId,CreatedAt) VALUES
            ('first','actor','user.ban','target','time'),('second','actor','invalid','target','time')
            """));
        Assert.That(db.Set<ModerationAuditRecord>().Count(),Is.EqualTo(1));Assert.That(Row(db,"preserved"),Is.EqualTo(before));
    }
    [Test]
    public void Upgrade_and_down_preserve_rows_index_and_original_guards()
    {
        using var c=new SqliteConnection("Data Source=:memory:");c.Open();using var db=Db(c);var migrator=db.GetService<IMigrator>();
        migrator.Migrate(Foundation);Insert(db,"history",reason:"policy");string before=Row(db,"history");
        db.Database.Migrate();Assert.That(Row(db,"history"),Is.EqualTo(before));
        Assert.Throws<SqliteException>(()=>Insert(db,"history",verb:"REPLACE"));
        Assert.Throws<SqliteException>(()=>Insert(db,"invalid","unknown"));
        using(var cmd=c.CreateCommand()){cmd.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='IX_moderation_audit_CreatedAt_Id'";Assert.That((long)cmd.ExecuteScalar()!,Is.EqualTo(1));}
        migrator.Migrate(Foundation);Assert.That(Row(db,"history"),Is.EqualTo(before));
        Assert.Throws<SqliteException>(()=>db.Database.ExecuteSqlRaw("UPDATE moderation_audit SET Action='user.unban'"));
        Assert.Throws<SqliteException>(()=>db.Database.ExecuteSqlRaw("DELETE FROM moderation_audit"));
        using(var cmd=c.CreateCommand()){cmd.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name IN ('moderation_audit_no_replace','moderation_audit_validate_insert')";Assert.That((long)cmd.ExecuteScalar()!,Is.Zero);}
        db.Database.Migrate();Assert.Throws<SqliteException>(()=>Insert(db,"history",verb:"REPLACE"));
    }
}
