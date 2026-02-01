using Microsoft.AspNetCore.Mvc;

namespace VilarDriverApi.Controllers
{
    [ApiController]
    [Route("files")]
    public class FilesController : ControllerBase
    {
        private readonly IWebHostEnvironment _env;

        public FilesController(IWebHostEnvironment env)
        {
            _env = env;
        }

        // GET /files/{*path}
        // Obsługuje: /files/invoices/xxx.pdf, /files/epod/xxx.pdf, itd.
        [HttpGet("{*path}")]
        public IActionResult GetFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return NotFound();

            // Normalizacja ścieżki
            path = path.Replace("\\", "/").TrimStart('/');

            // Blokada path traversal
            if (path.Contains(".."))
                return BadRequest(new { message = "Nieprawidłowa ścieżka." });

            var root = Path.Combine(_env.ContentRootPath, "files");
            var rootFull = Path.GetFullPath(root);

            var abs = Path.Combine(root, path);
            var absFull = Path.GetFullPath(abs);

            // Upewnij się, że nadal jesteśmy w katalogu "files"
            if (!absFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { message = "Nieprawidłowa ścieżka." });

            if (!System.IO.File.Exists(absFull))
                return NotFound();

            // U Ciebie pliki to PDF
            return PhysicalFile(absFull, "application/pdf");
        }
    }
}
