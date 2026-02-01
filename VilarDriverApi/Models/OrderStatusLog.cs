namespace VilarDriverApi.Models
{
    public class OrderStatusLog
    {
        public int Id { get; set; }

        public int OrderId { get; set; }
        public Order? Order { get; set; }

        public OrderStatus Status { get; set; }

        // ✅ STARE POLA – wymagane przez OrdersController.cs
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
        public double? Lat { get; set; }
        public double? Lng { get; set; }

        // ✅ NOWE POLA – potrzebne do historii (kto zmienił)
        public string ChangedByRole { get; set; } = ""; // "Driver" | "Admin"
        public int? ChangedByUserId { get; set; }
        public string? Note { get; set; }
    }
}