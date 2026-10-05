using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Reflection.Emit;

namespace TicketRace
{
    public class TicketDb(DbContextOptions<TicketDb> options) : DbContext(options)
    {
        public DbSet<Event> Events => Set<Event>();
        public DbSet<Seat> Seats => Set<Seat>();
        public DbSet<Order> Orders => Set<Order>();

        protected override void OnModelCreating(ModelBuilder b)
        {
            b.Entity<Event>(e =>
            {
                e.HasMany(ev => ev.Seats).WithOne(s => s.Event).HasForeignKey(s => s.EventId).OnDelete(DeleteBehavior.Restrict);
                e.Property(ev => ev.Name).IsRequired().HasMaxLength(200);
                // Event list ordered by soonest date.
                e.HasIndex(ev => ev.StartsAt);
            });

            b.Entity<Seat>(e =>
            {
                e.HasOne(s => s.Event).WithMany(ev => ev.Seats).HasForeignKey(s => s.EventId).OnDelete(DeleteBehavior.Restrict);
                // A seat physically exists once per event.
                e.HasIndex(s => new { s.EventId, s.Section, s.Row, s.Number }).IsUnique();
                // Serves "free seats of event X": equality on EventId+Status, ordered by Id.
                e.HasIndex(s => new { s.EventId, s.Status, s.Id });
                e.Property(s => s.Price).HasColumnType("REAL");
            });

            b.Entity<Order>(e =>
            {
                e.HasOne(o => o.Event).WithMany().HasForeignKey(o => o.EventId).OnDelete(DeleteBehavior.Restrict);
                e.HasOne(o => o.Seat).WithMany().HasForeignKey(o => o.SeatId).OnDelete(DeleteBehavior.Restrict);
                // Second line of defence: even if application code were wrong,
                // the database refuses a second order for the same seat.
                e.HasIndex(o => o.SeatId).IsUnique();
                e.Property(o => o.CustomerEmail).HasMaxLength(320);
            });

        }
    }

    public static class Seeder
    {
        public static async Task SeedAsync(TicketDb db)
        {
            if (await db.Events.AnyAsync()) return;

            var names = new[] { "Rock Night", "Jazz Evening", "Symphony Gala", "Stand-up Special", "Opera Premiere" };
            var sections = new[] { "A", "B", "C", "D" };       // 4 sections
            const int rows = 30, perRow = 20;                   // 4*30*20 = 2,400 seats per event

            await using var tx = await db.Database.BeginTransactionAsync();
            db.ChangeTracker.AutoDetectChangesEnabled = false;

            for (int i = 0; i < names.Length; i++)
            {
                var ev = new Event { Name = names[i], StartsAt = DateTime.UtcNow.Date.AddDays(30 + i * 7).AddHours(20) };
                foreach (var sec in sections)
                    for (int r = 1; r <= rows; r++)
                        for (int n = 1; n <= perRow; n++)
                            ev.Seats.Add(new Seat
                            {
                                Section = sec,
                                Row = r,
                                Number = n,
                                Price = sec == "A" ? 120m : sec == "B" ? 90m : 60m,
                            });
                db.Events.Add(ev);
            }
            await db.SaveChangesAsync();
            await tx.CommitAsync();   // 5 events x 2,400 = 12,000 seats
        }
    }
}
