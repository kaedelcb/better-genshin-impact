using BgiCoordinatorServer.RoomControl.Domain;
using Microsoft.EntityFrameworkCore;

namespace BgiCoordinatorServer.RoomControl.Persistence;

public class ControlRoomDbContext : DbContext
{
    public DbSet<StoredEvent> Events => Set<StoredEvent>();
    public DbSet<ControlRoom> ControlRooms => Set<ControlRoom>();
    public DbSet<ControlRoomMember> Members => Set<ControlRoomMember>();
    public DbSet<OnlineSession> OnlineSessions => Set<OnlineSession>();

    private readonly string _dbPath;

    public ControlRoomDbContext(string dbPath)
    {
        _dbPath = dbPath;
    }

    public ControlRoomDbContext(DbContextOptions<ControlRoomDbContext> options) : base(options)
    {
        _dbPath = "";
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            optionsBuilder.UseSqlite($"Data Source={_dbPath}");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<ControlRoom>(entity =>
        {
            entity.HasKey(e => e.RoomCode);
            entity.Property(e => e.OwnerUid).HasMaxLength(64);
            entity.Property(e => e.AllowedUids).HasConversion(
                v => string.Join(",", v),
                v => v.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList());
            entity.HasMany(e => e.Members).WithOne().HasForeignKey(e => e.RoomCode);
            entity.HasMany(e => e.OnlineSessions).WithOne().HasForeignKey(e => e.RoomCode);
        });

        modelBuilder.Entity<ControlRoomMember>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.RoomCode, e.PlayerUid }).IsUnique();
            entity.HasIndex(e => e.ConnectionId);

            entity.Property(e => e.ScheduledOnlineTime).HasMaxLength(8);
            entity.Property(e => e.ScheduledOnlineTimeFiredDate).HasMaxLength(10);
            entity.Property(e => e.OnlineHoeingGroupNames).HasConversion(
                v => string.Join("\n", v),
                v => v.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList());
            entity.Property(e => e.OnlineHoeingGroupTypes).HasConversion(
                v => string.Join("\n", v),
                v => v.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList());
            entity.Property(e => e.QuickCommands).HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                v => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new());
            entity.Property(e => e.OnlineHistory).HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                v => System.Text.Json.JsonSerializer.Deserialize<List<OnlineHistoryEntry>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new());
            entity.Property(e => e.ConfigGroups).HasConversion(
                v => string.Join("\n", v),
                v => v.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList());
            entity.Property(e => e.OneClickConfigs).HasConversion(
                v => string.Join("\n", v),
                v => v.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList());
            entity.Property(e => e.ConfigGroupTasks).HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                v => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<string>>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new());
            entity.Property(e => e.OneClickTasks).HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                v => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<string>>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new());
            entity.Property(e => e.Hotkeys).HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                v => System.Text.Json.JsonSerializer.Deserialize<List<object>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new());
        });

        modelBuilder.Entity<OnlineSession>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.RoomCode, e.State });

            entity.Property(e => e.ReadyMemberUids).HasConversion(
                v => string.Join(",", v),
                v => v.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList());
            entity.Property(e => e.ConfirmedMemberUids).HasConversion(
                v => string.Join(",", v),
                v => v.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList());
            entity.Property(e => e.ExecutedMemberUids).HasConversion(
                v => string.Join(",", v),
                v => v.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList());
            entity.Property(e => e.State).HasConversion<string>().HasMaxLength(32);
        });

        modelBuilder.Entity<StoredEvent>(entity =>
        {
            entity.HasKey(e => e.EventId);
            entity.HasIndex(e => new { e.AggregateId, e.Version }).IsUnique();
            entity.HasIndex(e => e.SequenceNumber);
            entity.Property(e => e.SequenceNumber).ValueGeneratedOnAdd();
        });
    }
}
