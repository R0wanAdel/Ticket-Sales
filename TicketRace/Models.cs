namespace TicketRace
{
    public enum SeatStatus { Free = 0, Reserved = 1 }

    public class Event
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public DateTime StartsAt { get; set; }
        public List<Seat> Seats { get; set; } = new();
    }

    public class Seat
    {
        public int Id { get; set; }
        public int EventId { get; set; }
        public Event Event { get; set; } = null!;
        public string Section { get; set; } = "";
        public int Row { get; set; }
        public int Number { get; set; }
        public decimal Price { get; set; }
        public SeatStatus Status { get; set; } = SeatStatus.Free;
    }

    public class Order
    {
        public int Id { get; set; }
        public int EventId { get; set; }
        public Event Event { get; set; } = null!;
        public int SeatId { get; set; }
        public Seat Seat { get; set; } = null!;
        public string CustomerEmail { get; set; } = "";
        public DateTime CreatedAt { get; set; }
    }

}

