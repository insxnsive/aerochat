using System.Net;
using System.IO;
using System.Net.Http;
using Aerochat.Connectivity;
using Aerochat.Presentation;

namespace Aerochat.VisualShell.Tests;

public sealed class ChatSendHeadersReadTests
{
    [TestCase(false,201)]
    [TestCase(false,204)]
    [TestCase(false,400)]
    [TestCase(false,500)]
    [TestCase(true,201)]
    [TestCase(true,400)]
    public async Task Status_only_send_never_reads_body_and_always_disposes(bool sticker,int status)
    {
        var content=new UnreadableContent();using var http=new HttpClient(new Handler(status,content));
        var client=new ChatMessageClient(http,new Uri("https://chat.invalid/"),"fixture-token");
        Exception? error=null;bool result=false;
        try { result=sticker ? await client.SendStickerAsync("fixture",StickerCatalog.Items[0].ResourceName)
            :await client.SendAsync("fixture","fixture-message"); }
        catch(Exception e){error=e;}
        Assert.That(content.Reads,Is.Zero);Assert.That(content.Disposed,Is.True);
        if(status<300){Assert.That(error,Is.Null);Assert.That(result,Is.True);}
        else Assert.That(error,Is.InstanceOf<HttpRequestException>());
    }
    private sealed class Handler(int status,HttpContent content):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)=>
            Task.FromResult(new HttpResponseMessage((HttpStatusCode)status){Content=content});
    }
    private sealed class UnreadableContent:HttpContent
    {
        internal int Reads;internal bool Disposed;
        protected override bool TryComputeLength(out long length){length=0;return false;}
        protected override Task SerializeToStreamAsync(Stream stream,TransportContext? context)
        {Reads++;throw new InvalidOperationException("Response body must not be read.");}
        protected override void Dispose(bool disposing){Disposed=true;base.Dispose(disposing);}
    }
}
