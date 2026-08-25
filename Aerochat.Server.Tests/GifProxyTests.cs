using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using Aerochat.Server.Auth;
using Aerochat.Server.Auth.OAuth;
using Aerochat.Server.Data;
using Aerochat.Server.Data.Entities;
using Aerochat.Server.Gifs;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aerochat.Server.Tests;

public sealed class GifProxyTests
{
    [Test]
    public async Task Search_maps_tenor_media_and_attribution_fields_and_encodes_query()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            capturedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    results = new[]
                    {
                        new
                        {
                            id = "tenor-1",
                            itemurl = "https://tenor.com/view/cat-1",
                            media_formats = new
                            {
                                gif = new { url = "https://media.example/full.gif" },
                                tinygif = new { url = "https://media.example/preview.gif" }
                            }
                        }
                    }
                })
            };
        });
        using var client = new HttpClient(handler);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tenor:ApiKey"] = "test-key",
                ["Tenor:BaseUrl"] = "https://tenor.example/v2"
            })
            .Build();
        var service = new TenorProxyService(client, configuration);

        TenorSearchResult result = await service.SearchAsync("cats & dogs", "medium", CancellationToken.None);

        Assert.That(result.ErrorCode, Is.Null);
        Assert.That(result.Items, Has.Count.EqualTo(1));
        GifSearchItemDto item = result.Items[0];
        Assert.Multiple(() =>
        {
            Assert.That(item.Id, Is.EqualTo("tenor-1"));
            Assert.That(item.PreviewUrl, Is.EqualTo("https://media.example/preview.gif"));
            Assert.That(item.Url, Is.EqualTo("https://media.example/full.gif"));
            Assert.That(item.AttributionUrl, Is.EqualTo("https://tenor.com/view/cat-1"));
            Assert.That(item.AttributionText, Is.EqualTo("Powered by Tenor"));
        });

        Assert.That(capturedRequest, Is.Not.Null);
        Assert.That(capturedRequest!.RequestUri!.AbsolutePath, Is.EqualTo("/v2/search"));
        var query = QueryHelpers.ParseQuery(capturedRequest.RequestUri.Query);
        Assert.Multiple(() =>
        {
            Assert.That(query["q"].ToString(), Is.EqualTo("cats & dogs"));
            Assert.That(query["contentfilter"].ToString(), Is.EqualTo("medium"));
            Assert.That(query["key"].ToString(), Is.EqualTo("test-key"));
        });
    }

    [Test]
    public async Task Upstream_failure_is_returned_as_proxy_failure()
    {
        using var client = new HttpClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.BadGateway)));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tenor:ApiKey"] = "test-key"
            })
            .Build();
        var service = new TenorProxyService(client, configuration);

        TenorSearchResult result = await service.SearchAsync("cats", "high", CancellationToken.None);

        Assert.That(result.Items, Is.Empty);
        Assert.That(result.ErrorCode, Is.EqualTo("gif_upstream_failed"));
    }

    [Test]
    public async Task Search_uses_high_content_filter_by_default()
    {
        HttpRequestMessage? capturedRequest = null;
        using var client = new HttpClient(new StubHttpMessageHandler(request =>
        {
            capturedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { results = Array.Empty<object>() })
            };
        }));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tenor:ApiKey"] = "test-key"
            })
            .Build();
        var service = new TenorProxyService(client, configuration);

        await service.SearchAsync("cats", null, CancellationToken.None);

        Assert.That(capturedRequest, Is.Not.Null);
        var query = QueryHelpers.ParseQuery(capturedRequest!.RequestUri!.Query);
        Assert.That(query["contentfilter"].ToString(), Is.EqualTo("high"));
    }

    [Test]
    public async Task Unauthenticated_request_gets_bearer_challenge()
    {
        using var factory = new ApiWebApplicationFactory();
        using HttpClient client = factory.CreateClient(new() { AllowAutoRedirect = false });

        using HttpResponseMessage response = await client.GetAsync("/gifs/search?q=cats");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(response.Headers.WwwAuthenticate.ToString(), Is.EqualTo("Bearer"));
            Assert.That(body, Is.EqualTo("{\"error\":\"unauthorized\"}"));
        });
    }

    [Test]
    public async Task Authenticated_request_without_tenor_key_gets_unavailable_error()
    {
        using var factory = new ApiWebApplicationFactory();
        Guid userId = Guid.NewGuid();
        await factory.SeedAsync(db =>
        {
            db.Users.Add(new ExternalUserEntity
            {
                Id = userId,
                Provider = "github",
                ProviderUserId = "gif-user",
                DisplayName = "GIF User",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            return Task.CompletedTask;
        });

        using HttpClient client = CreateAuthorizedClient(factory, "gif-user");
        using HttpResponseMessage response = await client.GetAsync("/gifs/search?q=cats");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(body, Is.EqualTo("{\"error\":\"gif_unavailable\"}"));
        });
    }

    [Test]
    public async Task Missing_query_gets_invalid_request_error()
    {
        using var factory = new ApiWebApplicationFactory();
        await factory.SeedAsync(db =>
        {
            db.Users.Add(new ExternalUserEntity
            {
                Id = Guid.NewGuid(),
                Provider = "github",
                ProviderUserId = "gif-invalid-query-user",
                DisplayName = "GIF User",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            return Task.CompletedTask;
        });

        using HttpClient client = CreateAuthorizedClient(factory, "gif-invalid-query-user");
        using HttpResponseMessage response = await client.GetAsync("/gifs/search");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(body, Is.EqualTo("{\"error\":\"invalid_request\"}"));
        });
    }

    [Test]
    public async Task Upstream_failure_is_mapped_to_bad_gateway_without_leaking_key()
    {
        using var factory = new ApiWebApplicationFactory
        {
            TenorApiKey = "fake-tenor-key",
            TenorRequestHandler = _ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        };
        await factory.SeedAsync(db =>
        {
            db.Users.Add(new ExternalUserEntity
            {
                Id = Guid.NewGuid(),
                Provider = "github",
                ProviderUserId = "gif-upstream-user",
                DisplayName = "GIF User",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            return Task.CompletedTask;
        });

        using HttpClient client = CreateAuthorizedClient(factory, "gif-upstream-user");
        using HttpResponseMessage response = await client.GetAsync("/gifs/search?q=cats");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadGateway));
            Assert.That(body, Is.EqualTo("{\"error\":\"gif_upstream_failed\"}"));
            Assert.That(body, Does.Not.Contain("fake-tenor-key"));
        });
    }

    [Test]
    public async Task Successful_search_returns_minimal_attributed_items()
    {
        using var factory = new ApiWebApplicationFactory
        {
            TenorApiKey = "test-key",
            TenorRequestHandler = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    results = new[]
                    {
                        new
                        {
                            id = "tenor-2",
                            itemurl = "https://tenor.com/view/dog-2",
                            media_formats = new
                            {
                                gif = new { url = "https://media.example/dog.gif" },
                                tinygif = new { url = "https://media.example/dog-preview.gif" }
                            }
                        }
                    }
                })
            }
        };
        await factory.SeedAsync(db =>
        {
            db.Users.Add(new ExternalUserEntity
            {
                Id = Guid.NewGuid(),
                Provider = "github",
                ProviderUserId = "gif-success-user",
                DisplayName = "GIF User",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            return Task.CompletedTask;
        });

        using HttpClient client = CreateAuthorizedClient(factory, "gif-success-user");
        using HttpResponseMessage response = await client.GetAsync(
            "/gifs/search?q=dogs%20%26%20cats&contentfilter=medium");
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument json = JsonDocument.Parse(body);
        JsonElement item = json.RootElement[0];

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(item.GetProperty("id").GetString(), Is.EqualTo("tenor-2"));
            Assert.That(item.GetProperty("previewUrl").GetString(), Is.EqualTo("https://media.example/dog-preview.gif"));
            Assert.That(item.GetProperty("url").GetString(), Is.EqualTo("https://media.example/dog.gif"));
            Assert.That(item.GetProperty("attributionUrl").GetString(), Is.EqualTo("https://tenor.com/view/dog-2"));
            Assert.That(item.GetProperty("attributionText").GetString(), Is.EqualTo("Powered by Tenor"));
            Assert.That(body, Does.Not.Contain("test-key"));
        });
    }

    private static HttpClient CreateAuthorizedClient(
        ApiWebApplicationFactory factory,
        string providerUserId)
    {
        HttpClient client = factory.CreateClient();
        string token = factory.Services.GetRequiredService<SessionService>()
            .Issue(new Identity("github", providerUserId, "Token Name"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
