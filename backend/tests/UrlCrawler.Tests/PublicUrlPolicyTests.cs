using UrlCrawler.Domain;

namespace UrlCrawler.Tests;

public sealed class PublicUrlPolicyTests
{
    [Theory]
    [InlineData("https://example.com/docs")]
    [InlineData("http://example.org/")]
    public void TryParse_accepts_absolute_public_http_urls(string value)
    {
        var accepted = PublicUrlPolicy.TryParse(value, out var uri, out var error);

        Assert.True(accepted);
        Assert.NotNull(uri);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("ftp://example.com")]
    [InlineData("http://localhost")]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://192.168.1.10")]
    [InlineData("https://user:password@example.com")]
    public void TryParse_rejects_non_http_or_non_public_urls(string value)
    {
        var accepted = PublicUrlPolicy.TryParse(value, out var uri, out var error);

        Assert.False(accepted);
        Assert.Null(uri);
        Assert.NotNull(error);
    }

    [Fact]
    public void Normalize_removes_fragments_and_default_ports_but_preserves_path_slashes()
    {
        var uri = new Uri("https://example.com:443/docs/?section=intro#top");

        var normalized = PublicUrlPolicy.Normalize(uri);

        Assert.Equal("https://example.com/docs/?section=intro", normalized);
    }

    [Fact]
    public void IsSameOrigin_requires_matching_scheme_host_and_port()
    {
        var root = new Uri("https://example.com/start");

        Assert.True(PublicUrlPolicy.IsSameOrigin(root, new Uri("https://example.com/next")));
        Assert.False(PublicUrlPolicy.IsSameOrigin(root, new Uri("http://example.com/next")));
        Assert.False(PublicUrlPolicy.IsSameOrigin(root, new Uri("https://example.com:8443/next")));
        Assert.False(PublicUrlPolicy.IsSameOrigin(root, new Uri("https://other.example/next")));
    }

    [Fact]
    public void DomainLinkRatio_is_zero_for_pages_without_links_and_counts_internal_links()
    {
        var page = new CrawledPage
        {
            Url = "https://example.com",
            UrlKey = "key",
            TotalLinks = 4,
            InternalLinks = 3
        };

        Assert.Equal(0.75, page.DomainLinkRatio);

        page.TotalLinks = 0;
        page.InternalLinks = 0;
        Assert.Equal(0, page.DomainLinkRatio);
    }
}
