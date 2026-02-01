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
    }
}