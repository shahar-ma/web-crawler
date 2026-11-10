using UrlCrawler.Domain;

namespace UrlCrawler.Worker;

public interface ICrawlProcessor
{
    Task ProcessAsync(CrawlRequested message, CancellationToken cancellationToken);
}
