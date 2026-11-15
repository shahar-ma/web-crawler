using Microsoft.AspNetCore.RateLimiting;
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
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(_ =>
{
    _.SwaggerDoc("v1", new Microsoft.OpenApi.OpenApiInfo
    {
        Version = "v1",
        Title = "UrlCrawler.Api",
    });
});
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy =>
    policy.WithOrigins(allowedOrigins).AllowAnyHeader().WithMethods("GET", "POST")));
builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.AddFixedWindowLimiter("create-job", o =>
        {
            o.PermitLimit = 10;
            o.Window = TimeSpan.FromMinutes(1);
        });
});
var app = builder.Build();

await InitializeDatabaseAsync(app);

if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Swagger:Enabled"))
{
    app.UseSwagger();
    app.UseSwaggerUI( _ =>
    {
        _.SwaggerEndpoint("/swagger/v1/swagger.json", "UrlCrawler.Api V1");
    });
}

app.UseCors();
app.UseRateLimiter();
app.MapHealthChecks("/health").DisableRateLimiting();
app.Map();

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

public partial class Program;
