using RabbitMQ.Client;

namespace UrlCrawler.Infrastructure;

public interface IRabbitConnectionProvider
{
    IConnection CreateConnection(string clientName);
}
