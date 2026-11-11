using System.Text;
using System.Text.Json;
using Aerochat.Server.Auth;
using Aerochat.Server.Auth.OAuth;
using Aerochat.Server.Calls;
using Aerochat.Server.Data;
using Aerochat.Server.Gateway;
using Microsoft.EntityFrameworkCore;

namespace Aerochat.Server.Rest;

public static class MeshEndpoints
{
    public sealed record Request(int Version,Guid? RoomId=null,Guid? To=null,Guid? EdgeId=null,
        string? Action=null,string? Sdp=null,string? Candidate=null);
    public static void MapMeshEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/conversations/{conversationId}/mesh",(Guid conversationId,HttpContext context,
            ChatDb db,IExternalUserStore users,SessionService sessions,MeshRooms rooms,CancellationToken ct)=>
            Apply("read",conversationId,context,null,db,users,sessions,rooms,null,256*1024,ct));
        foreach(string op in new[]{"join","leave","signal"})
        {
            string operation=op;
            endpoints.MapPost("/conversations/{conversationId}/mesh/"+op,(Guid conversationId,HttpContext context,
                Request request,ChatDb db,IExternalUserStore users,SessionService sessions,MeshRooms rooms,GatewayHub gateway,GatewayOptions options,CancellationToken ct)=>
                Apply(operation,conversationId,context,request,db,users,sessions,rooms,gateway,options.MaxFrameBytes,ct));
        }
    }
    private static async Task<IResult> Apply(string operation,Guid conversationId,HttpContext context,Request? request,
        ChatDb db,IExternalUserStore users,SessionService sessions,MeshRooms rooms,GatewayHub? gateway,int maxFrameBytes,CancellationToken ct)
    {
        var user=await ConversationAuth.TryGetCurrentUserAsync(context,sessions,users,ct);
        if(user is null)return ConversationAuth.Unauthorized(context);
        var conversation=await db.Conversations.AsNoTracking().SingleOrDefaultAsync(c=>c.Id==conversationId,ct);
        if(conversation is null)return ConversationAuth.NotFound();
        var members=await db.Participants.AsNoTracking().Where(p=>p.ConversationId==conversationId).Select(p=>p.UserId).ToArrayAsync(ct);
        if(!members.Contains(user.Id))return ConversationAuth.Forbidden();
        if(conversation.Kind!="group")return Error("mesh_group_required");
        context.Response.Headers.CacheControl="no-store";
        if(operation=="read")return Results.Ok(new{version=1,maxParticipants=4,room=rooms.Read(conversationId)});
        if(request is null||request.Version!=1)return Error("mesh_upgrade_required");
        if(request.RoomId==Guid.Empty)return ConversationAuth.InvalidRequest();
        if(operation=="signal" && (request.To is null || request.To==Guid.Empty || request.EdgeId is null ||
            request.EdgeId==Guid.Empty || request.RoomId is null || request.Action is not ("offer" or "answer" or "ice") ||
            (request.Action=="ice" ? string.IsNullOrWhiteSpace(request.Candidate) : string.IsNullOrWhiteSpace(request.Sdp)) ||
            new[]{request.Sdp,request.Candidate}.Any(s=>s is not null&&Encoding.UTF8.GetByteCount(s)>64*1024)))return ConversationAuth.InvalidRequest();
        // Same lock order as gateway presence callbacks. No roster transition can
        // overtake the audience selection and publish of another mesh request.
        return gateway!.WithPresenceLock<IResult>(()=>
        {
            if(operation=="signal")
            {
                if(!members.Contains(request.To!.Value))return ConversationAuth.Forbidden();
                var payload=JsonSerializer.SerializeToElement(new{conversationId,roomId=request.RoomId,edgeId=request.EdgeId,
                    from=user.Id,to=request.To,action=request.Action,sdp=request.Sdp,candidate=request.Candidate},new JsonSerializerOptions(JsonSerializerDefaults.Web));
                // Escaped JSON can be much larger than the UTF-8 SDP. Reserve envelope/cursor headroom.
                if(Encoding.UTF8.GetByteCount(payload.GetRawText())>maxFrameBytes-256)return ConversationAuth.InvalidRequest();
                string? error=rooms.Signal(conversationId,user.Id,request.To.Value,request.RoomId!.Value,
                    request.EdgeId!.Value,request.Action!);
                if(error is not null)return Error(error);
                gateway.Publish("mesh.signal",payload,[request.To.Value]);
                return Results.Ok();
            }
            if(operation=="leave"&&request.RoomId is null)return ConversationAuth.InvalidRequest();
            var result=operation=="join"?rooms.Join(conversationId,user.Id,request.RoomId):rooms.Leave(conversationId,user.Id,request.RoomId!.Value);
            if(result.Error is not null)return Error(result.Error);
            gateway.Publish("mesh.roster",JsonSerializer.SerializeToElement(new{conversationId,room=result.Snapshot},new JsonSerializerOptions(JsonSerializerDefaults.Web)),result.Snapshot.Participants);
            return Results.Ok(result.Snapshot);
        });
    }
    private static IResult Error(string code)=>Results.Json(new ErrorDto(code),statusCode:409);
}
