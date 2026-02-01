namespace VilarDriverApi.Models
{
    public class Vehicle
    {
        public int Id { get; set; }
        public int DriverId { get; set; }
        public string PlateNumber { get; set; } = "";

        public Driver? Driver { get; set; }
    }
}