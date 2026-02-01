namespace VilarDriverApi.Models
{
    public enum OrderStatus
    {
        Planned = 0,
        ToPickup = 1,
        Loaded = 2,
        ToDelivery = 3,
        Delivered = 4,
        Problem = 5
    }

    public class Order
    {
        public int Id { get; set; }
        public string OrderNumber { get; set; } = "";

        public bool IsPaid { get; set; }
        public DateTime? PaidUtc { get; set; }

        public string PickupAddress { get; set; } = "";
        public string DeliveryAddress { get; set; } = "";

        // Trzymamy jako UTC w bazie.
        // UI (React/Android) pokazuje lokalnie, a wysyła do backendu w ISO UTC.
        public DateTime? PickupTime { get; set; }
        public DateTime? DeliveryTime { get; set; }

        public string CargoInfo { get; set; } = "";

        public int? DriverId { get; set; }
        public Driver? Driver { get; set; }

        public OrderStatus Status { get; set; } = OrderStatus.Planned;

        public List<OrderStatusLog> StatusLogs { get; set; } = new();
        public EpodFile? EpodFile { get; set; }

        // ADMIN zamyka po weryfikacji ePOD -> wtedy znika kierowcy
        public bool IsCompletedByAdmin { get; set; } = false;
        public DateTime? CompletedUtc { get; set; }

        // ====== FAKTUROWANIE / ARCHIWUM ======
        public bool IsInvoiced { get; set; } = false;
        public DateTime? InvoicedUtc { get; set; }

        public bool IsArchived { get; set; } = false;
        public DateTime? ArchivedUtc { get; set; }

        // dane faktury
        public string ContractorName { get; set; } = "";

        // Termin płatności to zwykle data "biznesowa" (lokalna), więc nie wymuszamy tu UTC.
        public DateTime? PaymentDueDate { get; set; }

        // faktura pdf
        // przechowujemy relatywną ścieżkę np: invoices/invoice_12_20260118_121233.pdf
        public string? InvoicePdfRelativePath { get; set; }
    }
}
