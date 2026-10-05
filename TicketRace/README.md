# Ticket-Sales: Seat Reservation Race Demo

An ASP.NET Core minimal API (EF Core + SQLite) where many clients try to buy
the **same seat at the same time**. The goal: exactly one buyer wins, everyone
else gets a clean `409 Conflict`, and the database never holds two orders for
one seat.

## Project layout

| File | Purpose |
|------|---------|
| `Program.cs` | Endpoints: list events, list free seats, reserve a seat, inspect a seat |
| `Db.cs` | `TicketDb` context, indexes, and the seeder (5 events x 2,400 = 12,000 seats) |
| `ticketrace.db` | SQLite database (WAL mode) |

## Run the API

```bash
cd TicketRace
dotnet run
```

## Endpoints

| Method | Route | Result |
|--------|-------|--------|
| GET | `/events` | events with free-seat counts |
| GET | `/events/{eventId}/seats/free?limit=500&afterId=0` | keyset-paginated free seats |
| POST | `/events/{eventId}/seats/{seatId}/reserve` | `201` reserved, `404` no such seat, `409` already taken, `400` bad input |
| GET | `/seats/{seatId}` | seat status and number of orders for it |

## Where the race is and how it is closed

Two buyers can both see a seat as free and both try to take it. The reserve
endpoint decides the winner with **one atomic SQL statement**:

```sql
UPDATE Seats SET Status = 1 WHERE Id = @id AND EventId = @e AND Status = 0
```

- Check and change happen together inside the database. SQLite allows one
  writer at a time, so concurrent requests queue up.
- The winner's statement affects 1 row and goes on to insert the `Order`.
- Every loser re-evaluates `Status = 0` against the committed row, affects 0
  rows, rolls back, and returns `409`.
- Second line of defence: `Orders.SeatId` has a **unique index**
  (`IX_Orders_SeatId`), so even buggy application code cannot persist two
  orders for one seat.

## Demonstration

### 1. Fire concurrent requests at the API

Start the API, then send 50 parallel reservations for seat 1 of event 1:

```bash
seq 1 50 | xargs -P 50 -I{} curl -s -o /dev/null -w "%{http_code}\n" \
  -X POST http://localhost:5000/events/1/seats/1/reserve \
  -H "Content-Type: application/json" \
  -d '{"customerEmail":"user{}@test.com"}' | sort | uniq -c
```

Then confirm the final state:

```bash
curl -s http://localhost:5000/seats/1
```

What to expect from a correct run: one `201`, forty-nine `409`, and a seat
response with `"status":"Reserved"` and `"orders":1`.
(Adjust the port to whatever `dotnet run` prints.)

### 2. Reproduction against the project database (actual output)

The output below was **captured by running the same SQL against a copy of
`ticketrace.db`** with a small Python script (50 / 200 real threads, each with
its own SQLite connection). It exercises the database logic directly, not the
HTTP layer, and the figures come from that run.

**A. Naive check-then-update (read status, pause, then update).** Orders table
keeps its unique index:

```text
== NAIVE check-then-update: 50 threads, SAME seat (Id=1) ==
responses: {'201': 1, '500(unique idx)': 49}
orders=1  seats_with_>1_order=0  seats_reserved=1
```

The unique index saved the data, but 49 buyers hit an unhandled constraint
error instead of a clean `409`.

**B. Naive check-then-update with the unique index removed.** This shows the
race itself:

```text
== NAIVE, Orders.SeatId unique index DROPPED: 50 threads, SAME seat (Id=1) ==
responses: {'201': 50}
orders=50  seats_with_>1_order=1  seats_reserved=1
```

All 50 buyers were told "success" for one seat: **50 orders for a single
seat.**

**C. The project's atomic conditional `UPDATE`.**

```text
== ATOMIC conditional UPDATE: 50 threads, SAME seat (Id=1) ==
responses: {'201': 1, '409': 49}
orders=1  seats_with_>1_order=0  seats_reserved=1

== ATOMIC conditional UPDATE: 200 threads, 20 seats (10 per seat) ==
responses: {'201': 20, '409': 180}
orders=20  seats_with_>1_order=0  seats_reserved=20
```

Exactly one winner per seat, every loser gets `409`, and no seat has more than
one order.

## Verify the database yourself

```bash
sqlite3 ticketrace.db "SELECT SeatId, COUNT(*) FROM Orders GROUP BY SeatId HAVING COUNT(*) > 1;"
```

An empty result means no seat has been sold twice. The uploaded
`ticketrace.db` currently holds 5 events, 12,000 seats, and 1 order
(seat 1, `Reserved`), with no duplicates.


