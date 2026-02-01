using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using VilarDriverApi.Models;

namespace VilarDriverApi.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<User> Users => Set<User>();
        public DbSet<Driver> Drivers => Set<Driver>();
        public DbSet<Vehicle> Vehicles => Set<Vehicle>();
        public DbSet<Order> Orders => Set<Order>();
        public DbSet<OrderStatusLog> OrderStatusLogs => Set<OrderStatusLog>();
        public DbSet<EpodFile> EpodFiles => Set<EpodFile>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<User>()
                .HasOne(u => u.Driver)
                .WithOne(d => d.User!)
                .HasForeignKey<Driver>(d => d.UserId);

            modelBuilder.Entity<Driver>()
                .HasOne(d => d.Vehicle)
                .WithOne(v => v.Driver!)
                .HasForeignKey<Vehicle>(v => v.DriverId);

            // Order -> Driver (przypisanie/odpięcie)
            // Wymaga: Order.DriverId jako int? (nullable), żeby dało się odpiąć.
            modelBuilder.Entity<Order>()
                .HasOne(o => o.Driver)
                .WithMany(d => d.Orders)
                .HasForeignKey(o => o.DriverId)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<Order>()
                .HasIndex(o => o.DriverId);

            modelBuilder.Entity<OrderStatusLog>()
                .HasOne(l => l.Order)
                .WithMany(o => o.StatusLogs)
                .HasForeignKey(l => l.OrderId);

            modelBuilder.Entity<EpodFile>()
                .HasOne(e => e.Order)
                .WithOne(o => o.EpodFile)
                .HasForeignKey<EpodFile>(e => e.OrderId);

            // ===== UTC: twarda spójność dla pól UTC i pickup/delivery =====
            // SQLite nie zachowuje DateTime.Kind -> przy odczycie często dostajesz Unspecified.
            // Ten converter:
            // - przy zapisie wymusza UTC
            // - przy odczycie ustawia Kind=Utc, żeby JSON leciał jako "...Z"
            var utcDateTimeConverter = new ValueConverter<DateTime, DateTime>(
                v => ToUtc(v),
                v => DateTime.SpecifyKind(v, DateTimeKind.Utc)
            );

            var utcNullableDateTimeConverter = new ValueConverter<DateTime?, DateTime?>(
                v => v.HasValue ? ToUtc(v.Value) : v,
                v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v
            );

            // Order: pola *Utc + pickup/delivery jako UTC
            modelBuilder.Entity<Order>().Property(o => o.PaidUtc).HasConversion(utcNullableDateTimeConverter);
            modelBuilder.Entity<Order>().Property(o => o.CompletedUtc).HasConversion(utcNullableDateTimeConverter);
            modelBuilder.Entity<Order>().Property(o => o.InvoicedUtc).HasConversion(utcNullableDateTimeConverter);
            modelBuilder.Entity<Order>().Property(o => o.ArchivedUtc).HasConversion(utcNullableDateTimeConverter);

            modelBuilder.Entity<Order>().Property(o => o.PickupTime).HasConversion(utcNullableDateTimeConverter);
            modelBuilder.Entity<Order>().Property(o => o.DeliveryTime).HasConversion(utcNullableDateTimeConverter);

            // Logi/statusy: TimestampUtc
            modelBuilder.Entity<OrderStatusLog>().Property(l => l.TimestampUtc).HasConversion(utcDateTimeConverter);

            // ePOD: CreatedUtc
            modelBuilder.Entity<EpodFile>().Property(e => e.CreatedUtc).HasConversion(utcDateTimeConverter);

            // UWAGA:
            // PaymentDueDate NIE jest kończone na "Utc" i zwykle jest "datą lokalną" (termin płatności),
            // więc celowo NIE wymuszamy tu UTC na PaymentDueDate.
        }

        public override int SaveChanges(bool acceptAllChangesOnSuccess)
        {
            NormalizeUtcDateTimes();
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }

        public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            NormalizeUtcDateTimes();
            return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        private void NormalizeUtcDateTimes()
        {
            // Normalizujemy tylko:
            // - pola kończące się na "Utc"
            // - PickupTime / DeliveryTime (te też trzymamy jako UTC)
            // Dzięki temu np. PaymentDueDate (termin płatności) nie będzie niechcący przesuwany.
            foreach (var entry in ChangeTracker.Entries())
            {
                if (entry.State != EntityState.Added && entry.State != EntityState.Modified)
                    continue;

                foreach (var prop in entry.Properties)
                {
                    var clrType = prop.Metadata.ClrType;

                    if (clrType != typeof(DateTime) && clrType != typeof(DateTime?))
                        continue;

                    var name = prop.Metadata.Name;
                    var shouldNormalize =
                        name.EndsWith("Utc", StringComparison.Ordinal) ||
                        name == nameof(Order.PickupTime) ||
                        name == nameof(Order.DeliveryTime);

                    if (!shouldNormalize)
                        continue;

                    if (clrType == typeof(DateTime))
                    {
                        var dt = (DateTime)prop.CurrentValue!;
                        prop.CurrentValue = ToUtc(dt);
                    }
                    else
                    {
                        var dt = (DateTime?)prop.CurrentValue;
                        if (dt.HasValue)
                            prop.CurrentValue = ToUtc(dt.Value);
                    }
                }
            }
        }

        private static DateTime ToUtc(DateTime dt)
        {
            // Jeśli jest Unspecified (np. z datetime-local), przyjmujemy że to czas lokalny serwera
            // i konwertujemy do UTC. To pasuje do frontów: użytkownik wpisuje lokalnie, backend przechowuje UTC.
            if (dt.Kind == DateTimeKind.Utc)
                return dt;

            if (dt.Kind == DateTimeKind.Unspecified)
                dt = DateTime.SpecifyKind(dt, DateTimeKind.Local);

            return dt.ToUniversalTime();
        }
    }
}
