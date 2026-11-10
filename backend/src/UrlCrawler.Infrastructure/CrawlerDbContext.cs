using Microsoft.EntityFrameworkCore;
using UrlCrawler.Domain;

namespace UrlCrawler.Infrastructure;

public sealed class CrawlerDbContext(DbContextOptions<CrawlerDbContext> options) : DbContext(options)
{
    public DbSet<CrawlJob> Jobs => Set<CrawlJob>();
    public DbSet<CrawledPage> Pages => Set<CrawledPage>();
    public DbSet<CrawledLink> Links => Set<CrawledLink>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CrawlJob>(entity =>
        {
            entity.HasKey(job => job.Id);
            entity.Property(job => job.RootUrl).HasMaxLength(2048);
            entity.Property(job => job.FailureReason).HasMaxLength(500);
            if (Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite")
                entity.Property(job => job.StartedAt).HasConversion<long>();
            entity.HasIndex(job => new { job.CreatedAt, job.Id });
            entity.HasMany(job => job.Pages).WithOne().HasForeignKey(page => page.JobId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CrawledPage>(entity =>
        {
            entity.HasKey(page => page.Id);
            entity.Property(page => page.Url).HasMaxLength(2048);
            entity.Property(page => page.UrlKey).HasMaxLength(64);
            entity.Property(page => page.Title).HasMaxLength(500);
            entity.Property(page => page.Error).HasMaxLength(500);
            entity.HasIndex(page => new { page.JobId, page.UrlKey }).IsUnique();
            entity.Ignore(page => page.DomainLinkRatio);
            entity.HasMany(page => page.Links).WithOne().HasForeignKey(link => link.PageId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CrawledLink>(entity =>
        {
            entity.HasKey(link => link.Id);
            entity.Property(link => link.TargetUrl).HasMaxLength(2048);
            entity.Property(link => link.AnchorText).HasMaxLength(500);
            entity.HasIndex(link => new { link.PageId, link.IsInternal });
        });

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.HasKey(message => message.Id);
            entity.Property(message => message.Type).HasMaxLength(200);
            entity.HasIndex(message => new { message.PublishedAt, message.CreatedAt });
        });
    }
}
