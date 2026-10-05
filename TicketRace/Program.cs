using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using TicketRace;


    var builder = WebApplication.CreateBuilder(args);
    builder.Services.AddDbContext<TicketDb>(o => o.UseSqlite(builder.Configuration.GetConnectionString("Db")));
var app = builder.Build();
 
// Create schema, WAL mode (readers don't block the writer), seed.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<TicketDb>();
    await db.Database.EnsureCreatedAsync();
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    await Seeder.SeedAsync(db);
}

app.MapGet("/events", async (TicketDb db) =>
    await db.Events.AsNoTracking()
        .Select(e => new
        {
            e.Id,
            e.Name,
            e.StartsAt,
            FreeSeats = db.Seats.Count(s => s.EventId == e.Id && s.Status == SeatStatus.Free)
        })
        .ToListAsync());

// GET /events/{eventId}/seats/free?limit=500&afterId=0
// Keyset pagination: stable and O(limit) no matter how deep the client pages.
app.MapGet("/events/{eventId:int}/seats/free",
    async (int eventId, TicketDb db, int limit = 500, int afterId = 0) =>
    {
        limit = Math.Clamp(limit, 1, 5000);
        if (!await db.Events.AnyAsync(e => e.Id == eventId)) return Results.NotFound();

        var seats = await db.Seats.AsNoTracking()
            .Where(s => s.EventId == eventId && s.Status == SeatStatus.Free && s.Id > afterId)
            .OrderBy(s => s.Id)
            .Take(limit)
            .Select(s => new { s.Id, s.Section, s.Row, s.Number, s.Price })
            .ToListAsync();

        return Results.Ok(new
        {
            eventId,
            count = seats.Count,
            nextAfterId = seats.Count == limit ? seats[^1].Id : (int?)null,
            seats
        });
    });



// POST /events/{eventId}/seats/{seatId}/reserve   body: {"customerEmail":"a@b.c"}
// 201 reserved | 404 no such seat for this event | 409 already taken | 400 bad input
app.MapPost("/events/{eventId:int}/seats/{seatId:int}/reserve",
    async (int eventId, int seatId, [FromBody] ReserveRequest req, TicketDb db) =>
    {
        if (string.IsNullOrWhiteSpace(req.CustomerEmail) || !req.CustomerEmail.Contains('@'))
            return Results.BadRequest(new { error = "customerEmail is required" });

        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            // The whole race is decided by this ONE statement:
            //   UPDATE Seats SET Status = 1 WHERE Id = @id AND EventId = @e AND Status = 0
            // Check and change happen atomically inside the database. SQLite allows a
            // single writer at a time, so concurrent requests are serialised here; the
            // loser re-evaluates "Status = 0" against the committed row, matches 0 rows.
            // (It is deliberately the first statement in the transaction, so the write
            //  lock is taken immediately and Default Timeout makes waiters queue.)
            int claimed = await db.Seats
                .Where(s => s.Id == seatId && s.EventId == eventId && s.Status == SeatStatus.Free)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, SeatStatus.Reserved));

            if (claimed == 0)
            {
                await tx.RollbackAsync();
                bool exists = await db.Seats.AnyAsync(s => s.Id == seatId && s.EventId == eventId);
                return exists
                    ? Results.Conflict(new { error = "seat already reserved" })
                    : Results.NotFound(new { error = "seat not found for this event" });
            }

            var order = new Order
            {
                EventId = eventId,
                SeatId = seatId,
                CustomerEmail = req.CustomerEmail.Trim(),
                CreatedAt = DateTime.UtcNow
            };
            db.Orders.Add(order);
            await db.SaveChangesAsync();   // UNIQUE(Orders.SeatId) backs this up
            await tx.CommitAsync();

            return Results.Created($"/orders/{order.Id}",
                new { orderId = order.Id, eventId, seatId, order.CustomerEmail });
        }
        catch (DbUpdateException)   // unique-index violation: still a clean 409, never two orders
        {
            return Results.Conflict(new { error = "seat already reserved" });
        }
    });

app.MapGet("/seats/{seatId:int}", async (int seatId, TicketDb db) =>
{
    var s = await db.Seats.AsNoTracking().Where(x => x.Id == seatId)
        .Select(x => new { x.Id, x.EventId, Status = x.Status.ToString() }).FirstOrDefaultAsync();
    if (s is null) return Results.NotFound();
    var orders = await db.Orders.CountAsync(o => o.SeatId == seatId);
    return Results.Ok(new { s.Id, s.EventId, s.Status, orders });
});

app.Run();



public record ReserveRequest(string? CustomerEmail);