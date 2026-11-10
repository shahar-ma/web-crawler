using System.Net;
using UrlCrawler.Domain;

namespace UrlCrawler.Tests;

public sealed class PublicUrlPolicyEdgeTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://user:pass@example.com/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://0.0.0.0/")]
    [InlineData("http://169.254.1.1/")]
    [InlineData("http://172.31.0.1/")]
    [InlineData("http://100.64.0.1/")]
    [InlineData("http://192.0.2.1/")]
    [InlineData("http://198.19.1.1/")]
    [InlineData("http://203.0.113.1/")]
    [InlineData("http://224.0.0.1/")]
    [InlineData("http://[fc00::1]/")]
    [InlineData("http://[::ffff:127.0.0.1]/")]
    public void TryParse_rejects_invalid_or_reserved_addresses(string? value)
    {
        Assert.False(PublicUrlPolicy.TryParse(value, out var uri, out var error));
        Assert.Null(uri);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("http://8.8.8.8/")]
    [InlineData("http://1.1.1.1/")]
    [InlineData("http://[2001:4860:4860::8888]/")]
    public async Task EnsurePublicHostAsync_accepts_public_ip_addresses(string value)
    {
        await PublicUrlPolicy.EnsurePublicHostAsync(new Uri(value), CancellationToken.None);
    }

    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[::ffff:10.0.0.1]/")]
    public async Task EnsurePublicHostAsync_rejects_non_public_ip_addresses(string value)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PublicUrlPolicy.EnsurePublicHostAsync(new Uri(value), CancellationToken.None));
    }

    [Fact]
    public async Task EnsurePublicHostAsync_rejects_dns_names_resolving_to_private_addresses()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PublicUrlPolicy.EnsurePublicHostAsync(new Uri("http://localhost/"), CancellationToken.None));
    }

    [Fact]
    public void Normalize_removes_default_http_port_and_preserves_non_default_port()
    {
        Assert.Equal("http://example.com/path", PublicUrlPolicy.Normalize(new Uri("http://example.com:80/path#fragment")));
        Assert.Equal("https://example.com:8443/path", PublicUrlPolicy.Normalize(new Uri("https://example.com:8443/path")));
    }

    [Fact]
    public void IsSameOrigin_ignores_host_and_scheme_casing()
    {
        Assert.True(PublicUrlPolicy.IsSameOrigin(new Uri("https://EXAMPLE.com"), new Uri("HTTPS://example.COM/next")));
    }
}
