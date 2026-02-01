namespace VilarDriverApi.Models
{
    public class EpodFile
    {
        public int Id { get; set; }
        public int OrderId { get; set; }

        public string PdfRelativePath { get; set; } = "";
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        public double? Lat { get; set; }
        public double? Lng { get; set; }

        public Order? Order { get; set; }
    }
}