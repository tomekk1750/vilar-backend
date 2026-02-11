using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
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

        public AuthService(AppDbContext db, IConfiguration cfg)
        {
            _db = db;
            _cfg = cfg;
        }

        public static string HashPassword(string password)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(password));
            return Convert.ToHexString(bytes);
        }

        public async Task<(bool ok, string token, User? user)> LoginAsync(string login, string password)
        {
            var hash = HashPassword(password);

            var user = await _db.Users
                .Include(u => u.Driver)
                .ThenInclude(d => d!.Vehicle)
                .FirstOrDefaultAsync(u => u.Login == login && u.PasswordHash == hash);

            if (user == null) return (false, "", null);

            var token = CreateJwt(user);
            return (true, token, user);
        }

        // NEW: change password for currently logged-in user (by sub claim)
        // Returns false if current password is invalid OR user not found.
        public async Task<bool> ChangePasswordAsync(string sub, string currentPassword, string newPassword)
        {
            if (string.IsNullOrWhiteSpace(sub)) return false;
            if (!int.TryParse(sub, out var userId)) return false;

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null) return false;

            var currentHash = HashPassword(currentPassword);
            if (!string.Equals(user.PasswordHash, currentHash, StringComparison.OrdinalIgnoreCase))
                return false;

            user.PasswordHash = HashPassword(newPassword);
            await _db.SaveChangesAsync();
            return true;
        }

        private string CreateJwt(User user)
        {
            var jwt = _cfg.GetSection("Jwt");
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt["Key"]!));
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
