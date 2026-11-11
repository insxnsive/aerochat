using Aerochat.Server.Data;
using Aerochat.Server.Hardening;
using Microsoft.EntityFrameworkCore;

namespace Aerochat.Server.Moderation;

public sealed class ModerationSettings
{
    private readonly HashSet<Guid> _operators;
    public int AuditRetentionDays { get; }
    private ModerationSettings(HashSet<Guid> operators, int retention)
    { _operators=operators; AuditRetentionDays=retention; }
    public bool IsOperator(Guid user) => user != Guid.Empty && _operators.Contains(user);
    public static ModerationSettings FromConfiguration(IConfiguration config)
    {
        var users=new HashSet<Guid>();
        var section=config.GetSection("Moderation:OperatorIds");
        if (section.Value is not null) throw Invalid();
        foreach(var child in section.GetChildren())
        {
            if (!Guid.TryParse(child.Value,out var id) || id==Guid.Empty) throw Invalid();
            users.Add(id);
        }
        string? text=config["Moderation:AuditRetentionDays"];
        int days=90;
        if (text is not null && (!int.TryParse(text,out days) || days is < 1 or > 36500)) throw Invalid();
        return new(users,days);
    }
    private static InvalidOperationException Invalid() => new("Moderation configuration is invalid.");
}

// Separate from the normal message and TURN quotas; admission consumes a slot even if
// the later mutation is rejected. Only successful committed changes get audit rows.
public sealed class ModerationQuota(ModerationSettings settings, TimeProvider clock)
{
    private readonly FixedWindowRateLimiter _limiter=new(30,TimeSpan.FromSeconds(60),clock);
    public RateLimitDecision TryAcquire(Guid actor) => settings.IsOperator(actor)
        ? _limiter.TryAcquire(actor.ToString("N")) : new(false,TimeSpan.Zero);
}

public sealed class ModerationAuditRecord
{
    public Guid Id { get; set; }
    public Guid ActorId { get; set; }
    public string Action { get; set; } = "";
    public Guid TargetId { get; set; }
    public Guid? ConversationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? ReasonCode { get; set; }
}
public sealed class ServerBanRecord
{
    public Guid UserId { get; set; }
    public Guid OperatorId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

// Future mutation endpoints stage their state change and this record in one explicit
// transaction, then SaveChanges and commit once. This method never commits on its own.
public sealed class ModerationAudit(ChatDb db, TimeProvider clock)
{
    public void Append(Guid actor,string action,Guid target,Guid? conversation=null,string? reason=null)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("An audit transaction is required.");
        if (actor==Guid.Empty || target==Guid.Empty || conversation==Guid.Empty ||
            action is not ("message.delete" or "member.kick" or "user.ban" or "user.unban") ||
            reason is not (null or "spam" or "abuse" or "policy"))
            throw new ArgumentException("Invalid audit metadata.");
        db.Set<ModerationAuditRecord>().Add(new(){Id=Guid.NewGuid(),ActorId=actor,Action=action,
            TargetId=target,ConversationId=conversation,ReasonCode=reason,CreatedAt=clock.GetUtcNow()});
    }
    internal static void Configure(ModelBuilder model)
    {
        var audit=model.Entity<ModerationAuditRecord>();audit.ToTable("moderation_audit");audit.HasKey(a=>a.Id);
        audit.Property(a=>a.Id).HasConversion<string>();audit.Property(a=>a.ActorId).HasConversion<string>();
        audit.Property(a=>a.TargetId).HasConversion<string>();audit.Property(a=>a.ConversationId).HasConversion<string>();
        audit.Property(a=>a.CreatedAt).HasConversion(v=>v.ToUniversalTime().ToString("O"),v=>DateTimeOffset.Parse(v));
        audit.Property(a=>a.ReasonCode).IsRequired(false);audit.Property(a=>a.Action).IsRequired();audit.HasIndex(a=>new{a.CreatedAt,a.Id});
        var bans=model.Entity<ServerBanRecord>();bans.ToTable("server_bans");bans.HasKey(b=>b.UserId);
        bans.Property(b=>b.UserId).HasConversion<string>();bans.Property(b=>b.OperatorId).HasConversion<string>();
        bans.Property(b=>b.CreatedAt).HasConversion(v=>v.ToUniversalTime().ToString("O"),v=>DateTimeOffset.Parse(v));
    }
}
