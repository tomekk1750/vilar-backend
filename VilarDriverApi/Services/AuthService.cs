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

        private const int BcryptWorkFactor = 12;

        public AuthService(AppDbContext db, IConfiguration cfg)
        {
            _db = db;
            _cfg = cfg;
        }

        // Legacy (stare hashe w DB)
        public static string HashPasswordSha256(string password)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(password));
            return Convert.ToHexString(bytes);
        }
        public static string HashPassword(string password) => HashPasswordBcrypt(password);
        
        // Nowy standard
        public static string HashPasswordBcrypt(string password)
        {
            if (string.IsNullOrWhiteSpace(password))
                throw new ArgumentException("Password is required.", nameof(password));

            return BCrypt.Net.BCrypt.HashPassword(password, workFactor: BcryptWorkFactor);
        }

        private static bool LooksLikeBcrypt(string hash)
            => !string.IsNullOrWhiteSpace(hash) && hash.StartsWith("$2", StringComparison.Ordinal);

        private static bool LooksLikeSha256Hex(string hash)
        {
            // SHA256 hex = 64 znaki
            if (string.IsNullOrWhiteSpace(hash) || hash.Length != 64) return false;
            for (int i = 0; i < hash.Length; i++)
            {
                char c = hash[i];
                bool isHex =
                    (c >= '0' && c <= '9') ||
                    (c >= 'A' && c <= 'F') ||
                    (c >= 'a' && c <= 'f');
                if (!isHex) return false;
            }
            return true;
        }

        public async Task<(bool ok, string token, User? user)> LoginAsync(string login, string password)
        {
            var user = await _db.Users
                .Include(u => u.Driver)
                .ThenInclude(d => d!.Vehicle)
                .FirstOrDefaultAsync(u => u.Login == login);

            if (user == null) return (false, "", null);

            var hashInDb = user.PasswordHash ?? "";

            // 1) BCrypt (docelowo)
            if (LooksLikeBcrypt(hashInDb))
            {
                if (!BCrypt.Net.BCrypt.Verify(password, hashInDb))
                    return (false, "", null);

                var tokenOk = CreateJwt(user);
                return (true, tokenOk, user);
            }

            // 2) Legacy SHA256 (fallback)
            if (LooksLikeSha256Hex(hashInDb))
            {
                var legacyHash = HashPasswordSha256(password);
                if (!string.Equals(hashInDb, legacyHash, StringComparison.OrdinalIgnoreCase))
                    return (false, "", null);

                // ✅ Auto-upgrade do BCrypt po udanym loginie
                user.PasswordHash = HashPasswordBcrypt(password);
                await _db.SaveChangesAsync();

                var tokenOk = CreateJwt(user);
                return (true, tokenOk, user);
            }

            // 3) Nieznany format – traktujemy jako błąd
            return (false, "", null);
        }

        public async Task<bool> ChangePasswordAsync(string sub, string currentPassword, string newPassword)
        {
            if (string.IsNullOrWhiteSpace(sub)) return false;
            if (!int.TryParse(sub, out var userId)) return false;

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null) return false;

            // Weryfikacja “starego” hasła: obsłuż oba formaty
            var hashInDb = user.PasswordHash ?? "";

            bool ok;
            if (LooksLikeBcrypt(hashInDb))
            {
                ok = BCrypt.Net.BCrypt.Verify(currentPassword, hashInDb);
            }
            else if (LooksLikeSha256Hex(hashInDb))
            {
                ok = string.Equals(hashInDb, HashPasswordSha256(currentPassword), StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                ok = false;
            }

            if (!ok) return false;

            user.PasswordHash = HashPasswordBcrypt(newPassword);
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
