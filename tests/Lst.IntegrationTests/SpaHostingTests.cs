using System.Net;
using System.Text.Json;
using Lst.IntegrationTests.Infrastructure;

namespace Lst.IntegrationTests;

/// <summary>
/// Dilim 9: React SPA aynı origin'den sunulur. Kabuk (index.html) istemci rotalarına döner; API/health yolları kabuğa düşmez;
/// public takip rotası (<c>/t/{kod}</c>) referrer sızdırmaz, önbelleğe alınmaz ve kod log'a girmez.
/// </summary>
public sealed class SpaHostingTests : IDisposable
{
    private const string IndexMarker = "<!doctype html><title>lst-spa-test</title>";
    private readonly LstDatabaseFixture _db;
    private readonly string _dist;
    private readonly List<IDisposable> _disposables = [];

    public SpaHostingTests(LstDatabaseFixture db)
    {
        _db = db;
        _dist = Path.Combine(Path.GetTempPath(), "lst-spa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_dist, "assets"));
        File.WriteAllText(Path.Combine(_dist, "index.html"), IndexMarker);
        File.WriteAllText(Path.Combine(_dist, "assets", "app-abc123.js"), "export {};");
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        Directory.Delete(_dist, recursive: true);
    }

    private LstApiFactory Factory(bool withSpa = true)
    {
        var settings = withSpa ? new Dictionary<string, string?> { ["Spa:DistPath"] = _dist } : null;
        var factory = new LstApiFactory(_db, settings: settings);
        _disposables.Add(factory);
        return factory;
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/login")]
    [InlineData("/shipments")]
    [InlineData("/shipments/0192f3a4-7c1e-7b2a-9c11-2f4e5d6a7b8c")]
    [InlineData("/exceptions")]
    public async Task Client_routes_return_the_shell_without_authentication_and_with_document_security_headers(string path)
    {
        using var client = Factory().CreateHttpsClient();

        var response = await client.GetAsync(path, TestApi.Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(IndexMarker, await response.Content.ReadAsStringAsync(TestApi.Ct));
        Assert.True(response.Headers.CacheControl?.NoStore, "Cache-Control: no-store");
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        var csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("default-src 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("script-src 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", csp, StringComparison.Ordinal);
        // Kabuk oturum cookie'si veya antiforgery cookie'si üretmez.
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Public_tracking_route_is_not_indexed_or_cached_and_the_code_never_reaches_the_logs()
    {
        var factory = Factory();
        using var client = factory.CreateHttpsClient();
        const string code = "8M3QZ0V7K2XG9TJ4WD1R6HNB5C";

        var response = await client.GetAsync($"/t/{code}", TestApi.Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(IndexMarker, await response.Content.ReadAsStringAsync(TestApi.Ct));
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.Contains("noindex", Assert.Single(response.Headers.GetValues("X-Robots-Tag")), StringComparison.Ordinal);
        Assert.DoesNotContain(factory.Logs.Entries, entry => entry.Contains(code, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Hashed_assets_are_immutable_and_missing_files_are_not_replaced_by_the_shell()
    {
        using var client = Factory().CreateHttpsClient();

        var asset = await client.GetAsync("/assets/app-abc123.js", TestApi.Ct);
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.Equal("public, max-age=31536000, immutable", asset.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", Assert.Single(asset.Headers.GetValues("X-Content-Type-Options")));

        var index = await client.GetAsync("/index.html", TestApi.Ct);
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        Assert.True(index.Headers.CacheControl?.NoCache);
        Assert.True(index.Headers.Contains("Content-Security-Policy"));

        var missing = await client.GetAsync("/assets/missing-000000.js", TestApi.Ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.DoesNotContain("lst-spa-test", await missing.Content.ReadAsStringAsync(TestApi.Ct), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/api/does-not-exist")]
    [InlineData("/api")]
    [InlineData("/health/unknown")]
    [InlineData("/realtime/unknown")]
    [InlineData("/openapi/unknown")]
    public async Task Reserved_prefixes_never_fall_back_to_the_shell(string path)
    {
        using var client = Factory().CreateHttpsClient();

        var response = await client.GetAsync(path, TestApi.Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestApi.Ct);
        Assert.DoesNotContain("lst-spa-test", body, StringComparison.Ordinal);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(404, JsonDocument.Parse(body).RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Api_still_requires_authentication_and_non_get_requests_do_not_reach_the_shell()
    {
        using var client = Factory().CreateHttpsClient();

        var list = await client.GetAsync("/api/shipments", TestApi.Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
        Assert.Equal("unauthenticated", await TestApi.ProblemCodeAsync(list));

        var post = await client.PostAsync("/shipments", content: null, TestApi.Ct);
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        Assert.DoesNotContain("lst-spa-test", await post.Content.ReadAsStringAsync(TestApi.Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_static_output_only_the_api_is_served()
    {
        using var client = Factory(withSpa: false).CreateHttpsClient();

        var shell = await client.GetAsync("/shipments", TestApi.Ct);
        Assert.Equal(HttpStatusCode.NotFound, shell.StatusCode);

        var live = await client.GetAsync("/health/live", TestApi.Ct);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }
}
