using System.Net;
using System.Net.Http.Json;
using Integration.Contracts;
using Xunit;

namespace Integration.Tests;

public sealed class AuthenticationLimitsTests : IClassFixture<TestCluster>
{
    private readonly TestCluster cluster;
    public AuthenticationLimitsTests(TestCluster cluster) => this.cluster = cluster;

    [Fact]
    public async Task ExcessiveLoginAttemptsAreRateLimited()
    {
        using var browser = new HttpClient { BaseAddress = cluster.Client.BaseAddress };
        browser.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        using var setup = await browser.PostAsJsonAsync("/api/auth/setup", new Credentials("admin", "clave-inicial-segura"));
        Assert.Equal(HttpStatusCode.NoContent, setup.StatusCode);
        for (var attempt = 0; attempt < 9; attempt++)
        {
            using var response = await browser.PostAsJsonAsync("/api/auth/login", new Credentials("admin", "clave-incorrecta"));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        using var limited = await browser.PostAsJsonAsync("/api/auth/login", new Credentials("admin", "clave-incorrecta"));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }
}
