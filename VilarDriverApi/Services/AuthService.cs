using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using VilarDriverApi.Data;
using VilarDriverApi.Models;

namespace VilarDriverApi.Services
{
    public class AuthService
    {
        private readonly AppDbContext _db;
        private readonly IConfiguration _cfg;

        // 11–13 typowo OK. 12 to dobry kompromis.
        private const int BcryptWorkFactor = 12;

        public AuthService(AppDbContext db, IConfiguration cfg)
        {
            _db = db;
            _cfg = cfg;
        }

        // ✅ Publiczny helper (dla DbSeeder i innych miejsc w kodzie)
        public static string HashPassword(string password)
        {
            if (string.IsNullOrWhiteSpace(password))
                throw new ArgumentException("Password is required.", nameof(password));

            return BCrypt.Net.BCrypt.HashPassword(password, workFactor: BcryptWorkFactor);
        }

        private static bool VerifyPassword(string password, string passwordHash)
        {
            if (string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(passwordHash))
                return false;

            return BCrypt.Net.BCrypt.Verify(password, passwordHash);
        }

        public async Task<(bool ok, string token, User? user)> LoginAsync(string login, string password)
        {
            var user = await _db.Users
                .Include(u => u.Driver)
                .ThenInclude(d => d!.Vehicle)
                .FirstOrDefaultAsync(u => u.Login == login);

            if (user == null) return (false, "", null);

            if (!VerifyPassword(password, user.PasswordHash ?? ""))
                return (false, "", null);

            var token = CreateJwt(user);
            return (true, token, user);
        }

        // Change password for currently logged-in user (by sub claim)
        // Returns false if current password is invalid OR user not found.
        public async Task<bool> ChangePasswordAsync(string sub, string currentPassword, string newPassword)
        {
            if (string.IsNullOrWhiteSpace(sub)) return false;
            if (!int.TryParse(sub, out var userId)) return false;

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null) return false;

            if (!VerifyPassword(currentPassword, user.PasswordHash ?? ""))
                return false;

            user.PasswordHash = HashPassword(newPassword);
            await _db.SaveChangesAsync();
            return true;
        }

        private string CreateJwt(User user)
        {
            var jwt = _cfg.GetSection("Jwt");

            var keyStr = jwt["Key"];
            if (string.IsNullOrWhiteSpace(keyStr))
                throw new InvalidOperationException("Missing Jwt:Key configuration.");

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(keyStr));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var claims = new List<Claim>
            {
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim("role", user.Role.ToString())
            };

            if (user.Driver != null)
                claims.Add(new Claim("driverId", user.Driver.Id.ToString()));

            var token = new JwtSecurityToken(
                issuer: jwt["Issuer"],
                audience: jwt["Audience"],
                claims: claims,
                expires: DateTime.UtcNow.AddDays(3),
                signingCredentials: creds
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }
}
