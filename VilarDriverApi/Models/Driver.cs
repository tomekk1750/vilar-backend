namespace VilarDriverApi.Models
{
    public class Driver
    {
        public int Id { get; set; }
        public int UserId { get; set; }

        public string FullName { get; set; } = "";
        public string Phone { get; set; } = "";

        public User? User { get; set; }
        public Vehicle? Vehicle { get; set; }
        public List<Order> Orders { get; set; } = new();
    }
}