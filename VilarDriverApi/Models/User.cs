namespace VilarDriverApi.Models
{
    public enum UserRole { Admin = 0, Driver = 1 }

    public class User
    {
        public int Id { get; set; }
        public string Login { get; set; } = "";
        public string PasswordHash { get; set; } = "";
        public UserRole Role { get; set; } = UserRole.Driver;

        public Driver? Driver { get; set; }
    }
}