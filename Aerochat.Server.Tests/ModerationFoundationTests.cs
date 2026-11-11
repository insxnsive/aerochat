using Aerochat.Server.Data;
using Aerochat.Server.Moderation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Aerochat.Server.Tests;

public sealed class ModerationFoundationTests
{
    private static readonly Guid Actor=Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static ModerationSettings Settings(params (string,string)[] entries) => ModerationSettings.FromConfiguration(
        new ConfigurationBuilder().AddInMemoryCollection(entries.ToDictionary(e=>e.Item1,e=>(string?)e.Item2)).Build());
    [Test]
    public void Empty_allowlist_denies_and_retention_defaults_to_ninety()
    {var s=Settings();Assert.That(s.IsOperator(Actor),Is.False);Assert.That(s.AuditRetentionDays,Is.EqualTo(90));}
    [TestCase("Moderation:OperatorIds:0","private-invalid-value")]
    [TestCase("Moderation:OperatorIds:0","00000000-0000-0000-0000-000000000000")]
    [TestCase("Moderation:OperatorIds","private-invalid-value")]
    [TestCase("Moderation:AuditRetentionDays","0")]
    [TestCase("Moderation:AuditRetentionDays","bad")]
    public void Invalid_config_has_fixed_error(string key,string value)
    {Assert.That(Assert.Throws<InvalidOperationException>(()=>Settings((key,value)))!.Message,Is.EqualTo("Moderation configuration is invalid."));}
    [Test]
    public void Allowlist_and_separate_thirty_write_window_are_enforced()
    {
        var settings=Settings(("Moderation:OperatorIds:0",Actor.ToString()));var clock=new Clock();var q=new ModerationQuota(settings,clock);
        Assert.That(settings.IsOperator(Actor),Is.True);Assert.That(q.TryAcquire(Guid.NewGuid()).Allowed,Is.False);
        for(int i=0;i<30;i++)Assert.That(q.TryAcquire(Actor).Allowed,Is.True);
        var denied=q.TryAcquire(Actor);Assert.That(denied.Allowed,Is.False);Assert.That(denied.RetryAfter,Is.EqualTo(TimeSpan.FromSeconds(60)));
        clock.Now=clock.Now.AddSeconds(60);Assert.That(q.TryAcquire(Actor).Allowed,Is.True);
    }
    private sealed class Clock:TimeProvider
    {internal DateTimeOffset Now=DateTimeOffset.UnixEpoch;public override DateTimeOffset GetUtcNow()=>Now;}
    private static ChatDb Db(SqliteConnection c)=>new(new DbContextOptionsBuilder<ChatDb>().UseSqlite(c).Options);
    [Test]
    public async Task Migration_upgrade_rollback_and_audit_atomicity()
    {
        using var c=new SqliteConnection("Data Source=:memory:");c.Open();using var db=Db(c);db.Database.Migrate();
        var audit=new ModerationAudit(db,new Clock());
        Assert.Throws<InvalidOperationException>(()=>audit.Append(Actor,"user.ban",Actor));
        await using(var tx=await db.Database.BeginTransactionAsync())
        {
            db.Set<ServerBanRecord>().Add(new(){UserId=Actor,OperatorId=Actor,CreatedAt=DateTimeOffset.UnixEpoch});
            audit.Append(Actor,"user.ban",Actor);await db.SaveChangesAsync();await tx.RollbackAsync();
        }
        db.ChangeTracker.Clear();Assert.That(await db.Set<ModerationAuditRecord>().CountAsync(),Is.Zero);
        Assert.That(await db.Set<ServerBanRecord>().CountAsync(),Is.Zero);
        await using(var tx=await db.Database.BeginTransactionAsync())
        {audit.Append(Actor,"user.ban",Actor,reason:"policy");await db.SaveChangesAsync();await tx.CommitAsync();}
        db.ChangeTracker.Clear();Assert.That(await db.Set<ModerationAuditRecord>().CountAsync(),Is.EqualTo(1));
        Assert.Throws<SqliteException>(()=>db.Database.ExecuteSqlRaw("UPDATE moderation_audit SET Action='user.unban'"));
        Assert.Throws<SqliteException>(()=>db.Database.ExecuteSqlRaw("DELETE FROM moderation_audit"));
    }
    [Test]
    public void Audit_rejects_content_as_reason_and_unrecognized_action()
    {
        using var c=new SqliteConnection("Data Source=:memory:");c.Open();using var db=Db(c);db.Database.Migrate();
        using var tx=db.Database.BeginTransaction();var audit=new ModerationAudit(db,new Clock());
        Assert.Throws<ArgumentException>(()=>audit.Append(Actor,"user.ban",Actor,reason:"free-form message content"));
        Assert.Throws<ArgumentException>(()=>audit.Append(Actor,"unknown",Actor));
        Assert.That(db.ChangeTracker.Entries<ModerationAuditRecord>(),Is.Empty);
    }
}
