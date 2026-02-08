using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VilarDriverApi.Services;

namespace VilarDriverApi.Controllers
{
    [ApiController]
    [Route("api/auth")]
    public class AuthController : ControllerBase
    {
        private readonly AuthService _auth;

        public AuthController(AuthService auth) => _auth = auth;

        public record LoginRequest(string Login, string Password);

        public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest req)
        {
            var (ok, token, user) = await _auth.LoginAsync(req.Login, req.Password);
            if (!ok) return Unauthorized(new { message = "Błędny login lub hasło" });

            return Ok(new
            {
                token,
                userId = user!.Id,
                role = user.Role.ToString(),
                driverId = user.Driver?.Id,
                vehicle = user.Driver?.Vehicle?.PlateNumber
            });
        }

        // ✅ NEW: change password for the currently logged-in user
        [Authorize]
        [HttpPost("change-password")]
        public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest req)
        {
            var sub = User.FindFirst("sub")?.Value;
            if (string.IsNullOrWhiteSpace(sub))
                return Unauthorized(new { message = "Brak claim 'sub' w tokenie" });

            var ok = await _auth.ChangePasswordAsync(sub, req.CurrentPassword, req.NewPassword);
            if (!ok) return Unauthorized(new { message = "Aktualne hasło jest nieprawidłowe" });

            return Ok(new { message = "Hasło zostało zmienione" });
        }
    }
}
