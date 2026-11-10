using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Text.Json.Serialization;
using UrlCrawler.Api;
using UrlCrawler.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("CrawlerDb")
    ?? throw new InvalidOperationException("Configure the ConnectionStrings:CrawlerDb setting before starting the API.");

builder.Services.AddCrawlerInfrastructure(connectionString);
builder.Services.AddOutboxDispatcher();
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("sql-server", HealthStatus.Unhealthy);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins("http://localhost:5173").AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

await InitializeDatabaseAsync(app);

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.MapHealthChecks("/health");
ApiEndpoints.Map(app);

app.Run();

static async Task InitializeDatabaseAsync(WebApplication app)
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<CrawlerDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    for (var attempt = 1; attempt <= 12; attempt++)
    {
        try
        {
            await db.Database.EnsureCreatedAsync();
            return;
        }
        catch (Exception exception) when (attempt < 12)
        {
            logger.LogWarning(exception, "Database initialization attempt {Attempt} failed", attempt);
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
    }
}

public partial class Program { }
