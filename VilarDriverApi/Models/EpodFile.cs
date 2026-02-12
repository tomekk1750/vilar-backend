namespace VilarDriverApi.Models
{
public class EpodFile
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public string BlobName { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public double? Lat { get; set; }
    public double? Lng { get; set; }

    // NEW:
    public int Status { get; set; } = 0;          // 0 Pending, 1 Confirmed, 2 Failed
    public DateTime? UploadedUtc { get; set; }    // opcjonalnie
    public DateTime? ConfirmedUtc { get; set; }   // ustawiane w confirm

    public Order? Order { get; set; }
}
}