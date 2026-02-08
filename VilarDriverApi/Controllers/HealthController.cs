using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VilarDriverApi.Data;

namespace VilarDriverApi.Controllers
{
    [ApiController]
    [Route("api")]
    public class HealthController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly IWebHostEnvironment _env;

        public HealthController(AppDbContext db, IWebHostEnvironment env)
        {
            _db = db;
            _env = env;
        }

        // GET /api/health
        [HttpGet("health")]
        [HttpHead("health")]
        [AllowAnonymous]
        public async Task<IActionResult> Health()
        {
            var canConnect = await _db.Database.CanConnectAsync();

            return Ok(new
            {
                status = "ok",
                utcNow = DateTime.UtcNow,
                environment = _env.EnvironmentName,
                dbCanConnect = canConnect
            });
        }

        // GET /api/version
        [HttpGet("version")]
        [AllowAnonymous]
        public IActionResult Version()
        {
            var asm = Assembly.GetExecutingAssembly();
            var asmName = asm.GetName();
            var informationalVersion =
                asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            return Ok(new
            {
                app = asmName.Name,
                version = informationalVersion ?? asmName.Version?.ToString(),
                utcNow = DateTime.UtcNow,
                environment = _env.EnvironmentName
            });
        }
    }
}
