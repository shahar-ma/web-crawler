using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;

namespace UrlCrawler.Infrastructure;

public sealed class RabbitConnectionProvider(IConfiguration configuration) : IRabbitConnectionProvider
{
    private readonly ConnectionFactory _factory = new()
    {
        Uri = new Uri(configuration["RabbitMq:Uri"]
            ?? throw new InvalidOperationException("Configure the RabbitMq:Uri setting before starting the service.")),
        DispatchConsumersAsync = true,
        AutomaticRecoveryEnabled = true,
        NetworkRecoveryInterval = TimeSpan.FromSeconds(5)
    };

    public IConnection CreateConnection(string clientName) => _factory.CreateConnection(clientName);
}
