using Microsoft.EntityFrameworkCore;
using OfficeCal.Core.Entities;

namespace OfficeCal.Infrastructure;

public class OfficeCalDbContext : DbContext
{
    public OfficeCalDbContext(DbContextOptions<OfficeCalDbContext> options) : base(options) { }

    public DbSet<Department> Departments => Set<Department>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<EventOccurrence> EventOccurrences => Set<EventOccurrence>();
    public DbSet<EventAttendee> EventAttendees => Set<EventAttendee>();
    public DbSet<Notification> Notifications => Set<Notification>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Department>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(50).IsRequired();
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<User>(e =>
        {
            e.Property(x => x.EmployeeNo).HasMaxLength(20).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(50).IsRequired();
            e.Property(x => x.Email).HasMaxLength(100).IsRequired();
            e.Property(x => x.PasswordHash).HasMaxLength(200).IsRequired();
            e.Property(x => x.Role).HasMaxLength(20).HasConversion<string>().IsRequired();
            e.Property(x => x.IcsFeedToken).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.EmployeeNo).IsUnique();
            e.HasIndex(x => x.Email).IsUnique();
            e.HasIndex(x => x.IcsFeedToken).IsUnique();
            e.HasOne(x => x.Department).WithMany().HasForeignKey(x => x.DepartmentId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Room>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(50).IsRequired();
            e.Property(x => x.Location).HasMaxLength(100);
            e.Property(x => x.Equipment).HasMaxLength(200);
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<Event>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(100).IsRequired();
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.RecurrenceRule).HasMaxLength(500);
            e.Property(x => x.Status).HasMaxLength(20).HasConversion<string>().IsRequired();
            e.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId)
             .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Room).WithMany().HasForeignKey(x => x.RoomId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<EventOccurrence>(e =>
        {
            e.Property(x => x.TitleOverride).HasMaxLength(100);
            e.HasOne(x => x.Event).WithMany(x => x.Occurrences).HasForeignKey(x => x.EventId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Room).WithMany().HasForeignKey(x => x.RoomId)
             .OnDelete(DeleteBehavior.Restrict);

            // 一次發生只能有一列
            e.HasIndex(x => new { x.EventId, x.OriginalStartAt }).IsUnique();

            // 衝突偵測的主要查詢路徑
            e.HasIndex(x => new { x.RoomId, x.StartAt, x.EndAt })
             .HasDatabaseName("IX_EventOccurrences_Room_Range")
             .HasFilter("[IsCancelled] = 0 AND [RoomId] IS NOT NULL");

            // 行事曆區間查詢
            e.HasIndex(x => new { x.StartAt, x.EndAt });
        });

        b.Entity<EventAttendee>(e =>
        {
            e.HasKey(x => new { x.EventId, x.UserId });
            e.HasOne(x => x.Event).WithMany(x => x.Attendees).HasForeignKey(x => x.EventId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Notification>(e =>
        {
            e.Property(x => x.Type).HasMaxLength(30).HasConversion<string>().IsRequired();
            e.Property(x => x.Message).HasMaxLength(300).IsRequired();
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId)
             .OnDelete(DeleteBehavior.Cascade);
            // 事件被硬刪除時 EventId 設為 NULL
            e.HasOne<Event>().WithMany().HasForeignKey(x => x.EventId)
             .OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => new { x.UserId, x.IsRead, x.CreatedAt })
             .HasDatabaseName("IX_Notifications_User_Unread");
        });
    }
}
