using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VilarDriverApi.Data;
using VilarDriverApi.Models;
using VilarDriverApi.Services;

namespace VilarDriverApi.Controllers
{
    [ApiController]
    [Route("api/orders")]
    [Authorize]
    public class OrdersController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly EpodService _epod;

        public OrdersController(AppDbContext db, EpodService epod)
        {
            _db = db;
            _epod = epod;
        }

        private int? DriverId()
        {
            var claim = User.FindFirst("driverId")?.Value;
            return int.TryParse(claim, out var id) ? id : null;
        }

        private int? UserId()
        {
            // U Ciebie w JWT nie ma "userId" claim.
            // sub to userId -> ale zostawiam jak było, żeby nic nie zepsuć.
            var claim = User.FindFirst("userId")?.Value;
            return int.TryParse(claim, out var id) ? id : null;
        }

        private string? Role()
        {
            return User.FindFirst("role")?.Value;
        }

        private string BaseUrl()
        {
            return $"{Request.Scheme}://{Request.Host}";
        }

        private static string? ToFilesUrl(string baseUrl, string? relPath)
        {
            if (string.IsNullOrWhiteSpace(relPath)) return null;
            return $"{baseUrl}/files/{relPath}";
        }

        // ============================
        // Helpers: Problem info (SQLite-safe)
        // ============================

        private class ProblemInfo
        {
            public string? LastProblemNote { get; set; }
            public DateTime? LastProblemUtc { get; set; }
            public string? ProblemAtStatus { get; set; }
        }

        private async Task<Dictionary<int, ProblemInfo>> LoadProblemInfoForOrdersAsync(List<int> orderIds)
        {
            if (orderIds.Count == 0)
                return new Dictionary<int, ProblemInfo>();

            // WAŻNE: to jest SQLite-safe, bo to jest zwykłe SELECT + ORDER BY,
            // a "składanie" robimy w pamięci, nie w SQL (bez APPLY).
            var logs = await _db.OrderStatusLogs
                .AsNoTracking()
                .Where(l => orderIds.Contains(l.OrderId))
                .OrderByDescending(l => l.TimestampUtc)
                .Select(l => new
                {
                    l.OrderId,
                    l.Status,
                    l.TimestampUtc,
                    l.Note
                })
                .ToListAsync();

            var dict = new Dictionary<int, ProblemInfo>();

            foreach (var grp in logs.GroupBy(x => x.OrderId))
            {
                var list = grp.ToList(); // DESC

                int problemIdx = list.FindIndex(x =>
                    x.Status == OrderStatus.Problem &&
                    !string.IsNullOrWhiteSpace(x.Note)
                );

                if (problemIdx < 0)
                    continue;

                dict[grp.Key] = new ProblemInfo
                {
                    LastProblemNote = list[problemIdx].Note,
                    LastProblemUtc = list[problemIdx].TimestampUtc,
                    ProblemAtStatus = (problemIdx + 1 < list.Count)
                        ? list[problemIdx + 1].Status.ToString()
                        : null
                };
            }

            return dict;
        }

        // =========================================================
        // GET /api/orders/my
        // DRIVER: wszystkie swoje OTWARTE
        // =========================================================
        [HttpGet("my")]
        [Authorize(Roles = "Driver")]
        public async Task<IActionResult> My()
        {
            var driverId = DriverId();
            if (driverId == null) return Forbid();

            var baseUrl = BaseUrl();

            // 1) Pobierz zamówienia (SQLite-safe)
            var orders = await _db.Orders
                .AsNoTracking()
                .Include(o => o.EpodFile)
                .Where(o => o.DriverId == driverId.Value)
                .Where(o => !o.IsCompletedByAdmin)
                .OrderBy(o => o.PickupTime ?? o.DeliveryTime ?? DateTime.MaxValue)
                .Select(o => new
                {
                    o.Id,
                    o.OrderNumber,
                    o.PickupAddress,
                    o.DeliveryAddress,
                    o.PickupTime,
                    o.DeliveryTime,
                    o.CargoInfo,
                    o.DriverId,
                    status = o.Status.ToString(),
                    epodRelPath = o.EpodFile != null ? o.EpodFile.PdfRelativePath : null
                })
                .ToListAsync();

            // 2) Dociągnij info o problemie z logów (SQLite-safe)
            var ids = orders.Select(o => o.Id).ToList();
            var problemInfo = await LoadProblemInfoForOrdersAsync(ids);

            // 3) Sklej odpowiedź
            return Ok(orders.Select(o =>
            {
                problemInfo.TryGetValue(o.Id, out var p);

                return new
                {
                    o.Id,
                    o.OrderNumber,
                    o.PickupAddress,
                    o.DeliveryAddress,
                    o.PickupTime,
                    o.DeliveryTime,
                    o.CargoInfo,
                    o.DriverId,
                    o.status,
                    epodUrl = ToFilesUrl(baseUrl, o.epodRelPath),

                    // ✅ jeśli chcesz “wisienkę” (możesz też usunąć)
                    lastProblemNote = p?.LastProblemNote,
                    problemAtStatus = p?.ProblemAtStatus
                };
            }));
        }

        // =========================================================
        // GET /api/orders/today
        // =========================================================
        [HttpGet("today")]
        public async Task<IActionResult> Today()
        {
            var role = Role();

            IQueryable<Order> q = _db.Orders
                .Include(o => o.EpodFile)
                .AsNoTracking();

            if (role == "Driver")
            {
                var driverId = DriverId();
                if (driverId == null) return Forbid();
                q = q.Where(o => o.DriverId == driverId.Value);
            }
            else if (role == "Admin")
            {
                // bez filtra
            }
            else
            {
                return Forbid();
            }

            var today = DateTime.Today;
            var tomorrow = today.AddDays(1);

            q = q.Where(o =>
                o.PickupTime.HasValue &&
                o.PickupTime.Value >= today &&
                o.PickupTime.Value < tomorrow
            );

            var baseUrl = BaseUrl();

            // 1) Orders (SQLite-safe)
            var orders = await q
                .OrderBy(o => o.PickupTime ?? o.DeliveryTime ?? DateTime.MaxValue)
                .Select(o => new
                {
                    o.Id,
                    o.OrderNumber,
                    o.PickupAddress,
                    o.DeliveryAddress,
                    o.PickupTime,
                    o.DeliveryTime,
                    o.CargoInfo,
                    o.DriverId,
                    status = o.Status.ToString(),
                    epodRelPath = o.EpodFile != null ? o.EpodFile.PdfRelativePath : null
                })
                .ToListAsync();

            // 2) Problem info
            var ids = orders.Select(o => o.Id).ToList();
            var problemInfo = await LoadProblemInfoForOrdersAsync(ids);

            return Ok(orders.Select(o =>
            {
                problemInfo.TryGetValue(o.Id, out var p);

                return new
                {
                    o.Id,
                    o.OrderNumber,
                    o.PickupAddress,
                    o.DeliveryAddress,
                    o.PickupTime,
                    o.DeliveryTime,
                    o.CargoInfo,
                    o.DriverId,
                    o.status,
                    epodUrl = ToFilesUrl(baseUrl, o.epodRelPath),

                    lastProblemNote = p?.LastProblemNote,
                    problemAtStatus = p?.ProblemAtStatus
                };
            }));
        }

        // =========================================================
        // GET /api/orders/upcoming?days=7
        // =========================================================
        [HttpGet("upcoming")]
        [Authorize(Roles = "Driver")]
        public async Task<IActionResult> Upcoming([FromQuery] int days = 7)
        {
            if (days < 1) days = 1;
            if (days > 60) days = 60;

            var driverId = DriverId();
            if (driverId == null) return Forbid();

            var from = DateTime.Today;
            var to = DateTime.Today.AddDays(days + 1);

            var baseUrl = BaseUrl();

            // 1) Orders (SQLite-safe)
            var orders = await _db.Orders
                .AsNoTracking()
                .Include(o => o.EpodFile)
                .Where(o => o.DriverId == driverId.Value)
                .Where(o =>
                    (o.PickupTime.HasValue && o.PickupTime.Value >= from && o.PickupTime.Value < to) ||
                    (o.DeliveryTime.HasValue && o.DeliveryTime.Value >= from && o.DeliveryTime.Value < to)
                )
                .OrderBy(o => o.PickupTime ?? o.DeliveryTime ?? DateTime.MaxValue)
                .Select(o => new
                {
                    o.Id,
                    o.OrderNumber,
                    o.PickupAddress,
                    o.DeliveryAddress,
                    o.PickupTime,
                    o.DeliveryTime,
                    o.CargoInfo,
                    o.DriverId,
                    status = o.Status.ToString(),
                    epodRelPath = o.EpodFile != null ? o.EpodFile.PdfRelativePath : null
                })
                .ToListAsync();

            // 2) Problem info
            var ids = orders.Select(o => o.Id).ToList();
            var problemInfo = await LoadProblemInfoForOrdersAsync(ids);

            return Ok(orders.Select(o =>
            {
                problemInfo.TryGetValue(o.Id, out var p);

                return new
                {
                    o.Id,
                    o.OrderNumber,
                    o.PickupAddress,
                    o.DeliveryAddress,
                    o.PickupTime,
                    o.DeliveryTime,
                    o.CargoInfo,
                    o.DriverId,
                    o.status,
                    epodUrl = ToFilesUrl(baseUrl, o.epodRelPath),

                    lastProblemNote = p?.LastProblemNote,
                    problemAtStatus = p?.ProblemAtStatus
                };
            }));
        }

        // =========================================================
        // STATUS
        // =========================================================
        public record SetStatusRequest(int Status, double? Lat, double? Lng, string? Note);

        [HttpPost("{id:int}/status")]
        public async Task<IActionResult> SetStatus(int id, [FromBody] SetStatusRequest req)
        {
            var role = Role();

            int? driverId = null;
            if (role == "Driver")
            {
                driverId = DriverId();
                if (driverId == null) return Forbid();
            }
            else if (role != "Admin")
            {
                return Forbid();
            }

            var order = await _db.Orders.FirstOrDefaultAsync(o =>
                o.Id == id && (role == "Admin" || o.DriverId == driverId!.Value));

            if (order == null) return NotFound();

            if (!Enum.IsDefined(typeof(OrderStatus), req.Status))
                return BadRequest(new { message = "Nieprawidłowy status." });

            var newStatus = (OrderStatus)req.Status;

            var note = (req.Note ?? "").Trim();
            if (newStatus == OrderStatus.Problem && note.Length < 5)
                return BadRequest(new { message = "Opis problemu jest wymagany (min. 5 znaków)." });

            order.Status = newStatus;

            _db.OrderStatusLogs.Add(new OrderStatusLog
            {
                OrderId = order.Id,
                Status = order.Status,
                TimestampUtc = DateTime.UtcNow,
                Lat = req.Lat,
                Lng = req.Lng,
                ChangedByRole = role ?? "",
                ChangedByUserId = UserId(),
                Note = string.IsNullOrWhiteSpace(note) ? null : note
            });

            await _db.SaveChangesAsync();
            return Ok(new { message = "Status zapisany", status = order.Status.ToString() });
        }

        // =========================================================
        // ePOD (ZDJĘCIA -> PDF) - stary endpoint
        // =========================================================
        [HttpPost("{id:int}/epod")]
        [RequestSizeLimit(50_000_000)]
        public async Task<IActionResult> UploadEpod(
            int id,
            [FromForm] List<IFormFile> photos,
            [FromForm] double? lat,
            [FromForm] double? lng)
        {
            if (photos != null && photos.Any(p =>
                    Path.GetExtension(p.FileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase) ||
                    ((p.ContentType ?? "").Contains("pdf", StringComparison.OrdinalIgnoreCase))))
            {
                return BadRequest(new
                {
                    message = "Ten endpoint obsługuje tylko zdjęcia. Dla PDF użyj /api/orders/{id}/epod/upload i pola 'file'."
                });
            }

            var role = Role();

            int? driverId = null;
            if (role == "Driver")
            {
                driverId = DriverId();
                if (driverId == null) return Forbid();
            }
            else if (role != "Admin")
            {
                return Forbid();
            }

            var orderQuery = _db.Orders
                .Include(o => o.EpodFile)
                .AsQueryable();

            if (role == "Driver")
                orderQuery = orderQuery.Where(o => o.DriverId == driverId!.Value);

            var order = await orderQuery.FirstOrDefaultAsync(o => o.Id == id);

            if (order == null) return NotFound();
            if (photos == null || photos.Count == 0) return BadRequest(new { message = "Brak zdjęć" });

            var pdfRel = await _epod.BuildPdfFromPhotosAsync(order.Id, photos);

            UpsertEpodFile(order, pdfRel, lat, lng);
            await _db.SaveChangesAsync();

            var url = $"{Request.Scheme}://{Request.Host}/files/{pdfRel}";
            return Ok(new { message = "ePOD wygenerowany", pdfUrl = url });
        }

        // =========================================================
        // ePOD (PDF lub zdjęcia -> zawsze link do PDF)
        // POST /api/orders/{id}/epod/upload
        // =========================================================
        public class EpodUploadForm
        {
            public IFormFile? File { get; set; }              // PDF
            public List<IFormFile>? Photos { get; set; }      // zdjęcia
            public double? Lat { get; set; }
            public double? Lng { get; set; }
        }

        [HttpPost("{id:int}/epod/upload")]
        [RequestSizeLimit(50_000_000)]
        public async Task<IActionResult> UploadEpodUpload(int id, [FromForm] EpodUploadForm form)
        {
            var role = Role();

            int? driverId = null;
            if (role == "Driver")
            {
                driverId = DriverId();
                if (driverId == null) return Forbid();
            }
            else if (role != "Admin")
            {
                return Forbid();
            }

            var orderQuery = _db.Orders
                .Include(o => o.EpodFile)
                .AsQueryable();

            if (role == "Driver")
                orderQuery = orderQuery.Where(o => o.DriverId == driverId!.Value);

            var order = await orderQuery.FirstOrDefaultAsync(o => o.Id == id);
            if (order == null) return NotFound();

            // 1) PDF
            if (form.File != null && form.File.Length > 0)
            {
                var ext = Path.GetExtension(form.File.FileName).ToLowerInvariant();
                var ct = (form.File.ContentType ?? "").ToLowerInvariant();
                var isPdf = ct.Contains("pdf") || ext == ".pdf";

                if (!isPdf)
                    return BadRequest(new { message = "Pole 'file' obsługuje tylko PDF. Dla zdjęć użyj 'photos'." });

                var pdfRel = await _epod.SavePdfAsync(order.Id, form.File);

                UpsertEpodFile(order, pdfRel, form.Lat, form.Lng);
                await _db.SaveChangesAsync();

                var url = $"{Request.Scheme}://{Request.Host}/files/{pdfRel}";
                return Ok(new { message = "PDF zapisany", pdfUrl = url });
            }

            // 2) Zdjęcia
            var photos = form.Photos ?? new List<IFormFile>();
            if (photos.Count == 0)
                return BadRequest(new { message = "Wyślij PDF w polu 'file' albo zdjęcia w polu 'photos'." });

            if (photos.Any(p =>
                    Path.GetExtension(p.FileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase) ||
                    ((p.ContentType ?? "").Contains("pdf", StringComparison.OrdinalIgnoreCase))))
            {
                return BadRequest(new { message = "PDF wyślij w polu 'file', nie w 'photos'." });
            }

            var pdfRelFromPhotos = await _epod.BuildPdfFromPhotosAsync(order.Id, photos);

            UpsertEpodFile(order, pdfRelFromPhotos, form.Lat, form.Lng);
            await _db.SaveChangesAsync();

            var pdfUrl = $"{Request.Scheme}://{Request.Host}/files/{pdfRelFromPhotos}";
            return Ok(new { message = "ePOD wygenerowany", pdfUrl });
        }

        private void UpsertEpodFile(Order order, string pdfRel, double? lat, double? lng)
        {
            if (order.EpodFile == null)
            {
                order.EpodFile = new EpodFile
                {
                    OrderId = order.Id,
                    PdfRelativePath = pdfRel,
                    CreatedUtc = DateTime.UtcNow,
                    Lat = lat,
                    Lng = lng
                };
            }
            else
            {
                order.EpodFile.PdfRelativePath = pdfRel;
                order.EpodFile.CreatedUtc = DateTime.UtcNow;
                order.EpodFile.Lat = lat;
                order.EpodFile.Lng = lng;
            }
        }
    }
}
