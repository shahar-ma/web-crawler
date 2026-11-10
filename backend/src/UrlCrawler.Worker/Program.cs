using System.Net;
using UrlCrawler.Infrastructure;
using UrlCrawler.Worker;

var builder = Host.CreateApplicationBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("CrawlerDb")
    ?? throw new InvalidOperationException("Configure the ConnectionStrings:CrawlerDb setting before starting the worker.");

builder.Services.AddCrawlerInfrastructure(connectionString);
builder.Services.AddSingleton(new HttpClient(new HttpClientHandler
{
    AllowAutoRedirect = false,
    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
})
{
    Timeout = TimeSpan.FromSeconds(12)
});
builder.Services.AddScoped<ICrawlProcessor, CrawlProcessor>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
