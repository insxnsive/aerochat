using Aerochat.Server.Turn;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

namespace Aerochat.Server.Tests;

public sealed class TurnPlaceholderSecretTests
{
    [TestCase("<externally-provisioned-shared-secret>")]
    [TestCase("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa<")]
    [TestCase("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa>")]
    [TestCase("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaPLACEHOLDER")]
    [TestCase("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaExample")]
    [TestCase("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaChangeMe")]
    [TestCase("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaCHANGE-ME")]
    [TestCase("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaChange_Me")]
    public void Placeholder_secret_fails_startup_with_sanitized_error(string secret)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Turn:Urls:0"] = "turn:relay.example:3478?transport=udp",
            ["Turn:SharedSecret"] = secret,
            ["ConnectionStrings:Chat"] = "Data Source=:memory:"
        });
        var error = Assert.Throws<InvalidOperationException>(() => ServerComposition.ConfigureBuilder(builder));
        Assert.That(error!.Message, Is.EqualTo("Invalid TURN configuration."));
        Assert.That(error.InnerException, Is.Null);
        Assert.That(error.ToString(), Does.Not.Contain(secret));
    }
    [TestCase("m7Kq9N2vR4xT8zB6dF3hJ5pL0sW1yC9a")]
    [TestCase("test-only-turn-secret-0123456789abcdef")]
    public void Non_placeholder_fixture_remains_accepted(string secret)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Turn:Urls:0"] = "turn:relay.example:3478?transport=udp", ["Turn:SharedSecret"] = secret
        }).Build();
        Assert.That(TurnSettings.FromConfiguration(config).Urls, Has.Count.EqualTo(1));
    }
}
