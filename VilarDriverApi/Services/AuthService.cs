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
                expires: DateTime.UtcNow.AddDays(7),
                signingCredentials: creds
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }
}