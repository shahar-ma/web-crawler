using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace UrlCrawler.Infrastructure;

public static class InfrastructureExtensions
{
    public static IServiceCollection AddCrawlerInfrastructure(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddDbContext<CrawlerDbContext>(options =>
            options.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure()));
        services.AddSingleton<RabbitConnectionProvider>();
        services.AddSingleton<IRabbitConnectionProvider>(provider => provider.GetRequiredService<RabbitConnectionProvider>());
        return services;
    }

    public static IServiceCollection AddOutboxDispatcher(this IServiceCollection services)
    {
        services.AddHostedService<OutboxDispatcher>();
        return services;
    }
}
