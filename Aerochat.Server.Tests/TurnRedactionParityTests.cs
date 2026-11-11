using System.Text.Json;
using Aerochat.Server.Turn;

namespace Aerochat.Server.Tests;

public sealed class TurnRedactionParityTests
{
    [Test]
    public void Record_diagnostics_and_copy_diagnostics_do_not_contain_credentials()
    {
        var server=new TurnIceServer(["turn:private.example"],"private-user","private-credential");
        var response=new TurnCredentialsResponse([server],600);
        foreach(string text in new[]{server.ToString(),(server with{}).ToString(),response.ToString(),(response with{}).ToString()})
        {
            Assert.That(text,Does.Contain("[redacted]"));
            Assert.That(text,Does.Not.Contain("private-user").And.Not.Contain("private-credential").And.Not.Contain("private.example"));
        }
    }
    [Test]
    public void Wire_response_keeps_credentials_urls_and_ttl()
    {
        var server=new TurnIceServer(["turn:private.example"],"private-user","private-credential");
        string json=JsonSerializer.Serialize(new TurnCredentialsResponse([server],600),new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc=JsonDocument.Parse(json);var root=doc.RootElement;
        Assert.That(root.GetProperty("ttl").GetInt32(),Is.EqualTo(600));
        var item=root.GetProperty("iceServers")[0];
        Assert.That(item.GetProperty("urls")[0].GetString(),Is.EqualTo("turn:private.example"));
        Assert.That(item.GetProperty("username").GetString(),Is.EqualTo("private-user"));
        Assert.That(item.GetProperty("credential").GetString(),Is.EqualTo("private-credential"));
    }
}
