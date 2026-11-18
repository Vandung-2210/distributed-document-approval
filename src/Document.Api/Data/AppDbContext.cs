using Document.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Document.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<DocumentItem> Documents => Set<DocumentItem>();
    public DbSet<DocumentHistory> DocumentHistories => Set<DocumentHistory>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DocumentItem>(entity =>
        {
            entity.HasKey(d => d.Id);
            // Lưu Enum dưới dạng chuỗi (Draft, Submitted...) cho dễ đọc trong DB và JSON
            entity.Property(d => d.Status).HasConversion<string>();
            entity.HasMany(d => d.Histories)
                  .WithOne()
                  .HasForeignKey(h => h.DocumentId)
                  .OnDelete(DeleteBehavior.Cascade);
        });
    }
}