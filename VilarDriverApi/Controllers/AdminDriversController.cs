using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VilarDriverApi.Data;
using VilarDriverApi.Models;
using VilarDriverApi.Services;

namespace VilarDriverApi.Controllers
{
    [ApiController]
    [Route("api/admin/drivers")]
    [Authorize(Roles = "Admin")]
    public class AdminDriversController : ControllerBase
    {
        private readonly AppDbContext _db;

        public AdminDriversController(AppDbContext db)
        {
            _db = db;
        }

        // GET /api/admin/drivers
        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            // Łączymy Driver -> User po UserId, żeby zwrócić login
            var drivers = await _db.Drivers
                .AsNoTracking()
                .OrderBy(d => d.FullName)
                .Select(d => new
                {
                    id = d.Id,
                    name = d.FullName,
                    phone = d.Phone,
                    login = d.User != null ? d.User.Login : "" // wymaga nawigacji Driver.User (jeśli jest)
                })
                .ToListAsync();

            return Ok(drivers);
        }

        // ============================
        // CREATE DRIVER (User + Driver)
        // POST /api/admin/drivers
        // ============================

        public class CreateDriverRequest
        {
            public string Login { get; set; } = "";
            public string Password { get; set; } = "";
            public string FullName { get; set; } = "";
            public string Phone { get; set; } = "";
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] CreateDriverRequest req)
        {
            var login = (req.Login ?? "").Trim();
            var password = req.Password ?? "";
            var fullName = (req.FullName ?? "").Trim();
            var phone = (req.Phone ?? "").Trim();

            if (string.IsNullOrWhiteSpace(login))
                return BadRequest(new { message = "Login jest wymagany." });

            if (string.IsNullOrWhiteSpace(password) || password.Length < 6)
                return BadRequest(new { message = "Hasło musi mieć min. 6 znaków." });

            if (string.IsNullOrWhiteSpace(fullName))
                return BadRequest(new { message = "Imię i nazwisko kierowcy jest wymagane." });

            var exists = await _db.Users.AnyAsync(u => u.Login == login);
            if (exists)
                return BadRequest(new { message = "Taki login już istnieje." });

            await using var tx = await _db.Database.BeginTransactionAsync();

            try
            {
                var user = new User
                {
                    Login = login,
                    PasswordHash = AuthService.HashPassword(password),
                    Role = UserRole.Driver
                };

                _db.Users.Add(user);
                await _db.SaveChangesAsync(); // żeby dostać user.Id

                var driver = new Driver
                {
                    UserId = user.Id,
                    FullName = fullName,
                    Phone = phone
                };

                _db.Drivers.Add(driver);
                await _db.SaveChangesAsync();

                await tx.CommitAsync();

                return Ok(new
                {
                    message = "Utworzono kierowcę",
                    driver = new
                    {
                        id = driver.Id,
                        name = driver.FullName,
                        phone = driver.Phone,
                        userId = user.Id,
                        login = user.Login
                    }
                });
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        }

        // ============================
        // RESET DRIVER PASSWORD
        // POST /api/admin/drivers/{id}/reset-password
        // ============================

        public class ResetPasswordRequest
        {
            public string NewPassword { get; set; } = "";
        }

        [HttpPost("{id:int}/reset-password")]
        public async Task<IActionResult> ResetPassword(int id, [FromBody] ResetPasswordRequest req)
        {
            var newPassword = (req?.NewPassword ?? "").Trim();

            if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 6)
                return BadRequest(new { message = "Hasło musi mieć min. 6 znaków." });

            var driver = await _db.Drivers
                .Include(d => d.User)
                .FirstOrDefaultAsync(d => d.Id == id);

            if (driver == null)
                return NotFound(new { message = "Nie znaleziono kierowcy." });

            if (driver.User == null)
                return BadRequest(new { message = "Kierowca nie ma przypisanego konta użytkownika." });

            if (driver.User.Role != UserRole.Driver)
                return BadRequest(new { message = "To konto nie jest kontem kierowcy." });

            driver.User.PasswordHash = AuthService.HashPassword(newPassword);
            await _db.SaveChangesAsync();

            return Ok(new { message = "Hasło kierowcy zostało zresetowane." });
        }
    }
}
