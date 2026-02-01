using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VilarDriverApi.Data;
using VilarDriverApi.Models;

namespace VilarDriverApi.Controllers
{
    [ApiController]
    [Route("api/admin/orders")]
    [Authorize(Roles = "Admin")]
    public class AdminOrdersController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly IWebHostEnvironment _env;

        public AdminOrdersController(AppDbContext db, IWebHostEnvironment env)
        {
            _db = db;
            _env = env;
        }

        // ============================
        // Helpers: auth/user id
        // ============================
        private int? UserId()
        {
            var claim = User.FindFirst("userId")?.Value ?? User.FindFirst("sub")?.Value;
            return int.TryParse(claim, out var id) ? id : null;
        }

        // ============================
        // Helpers: Problem info
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
                var list = grp.ToList();
                int problemIdx = list.FindIndex(x => x.Status == OrderStatus.Problem);

                if (problemIdx < 0)
                    continue;

                var info = new ProblemInfo
                {
                    LastProblemNote = string.IsNullOrWhiteSpace(list[problemIdx].Note) ? null : list[problemIdx].Note,
                    LastProblemUtc = list[problemIdx].TimestampUtc,
                    ProblemAtStatus = (problemIdx + 1 < list.Count)
                        ? list[problemIdx + 1].Status.ToString()
                        : null
                };

                dict[grp.Key] = info;
            }

            return dict;
        }

        private string BaseUrl() => $"{Request.Scheme}://{Request.Host}";

        // ============================
        // Helpers: auto order number
        // ============================
        private const string OrderNumberPrefix = "Z-";

        private async Task<string> GenerateNextOrderNumberAsync()
        {
            var last = await _db.Orders
                .AsNoTracking()
                .Where(o => o.OrderNumber.StartsWith(OrderNumberPrefix))
                .OrderByDescending(o => o.OrderNumber)
                .Select(o => o.OrderNumber)
                .FirstOrDefaultAsync();

            var next = 1;

            if (!string.IsNullOrWhiteSpace(last))
            {
                var numPart = last.Substring(OrderNumberPrefix.Length);
                if (int.TryParse(numPart, out var parsed))
                    next = parsed + 1;
            }

            return $"{OrderNumberPrefix}{next:D5}";
        }

        // ============================
        // GET /api/admin/orders
        // ============================
        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var orders = await _db.Orders
                .AsNoTracking()
                .Include(o => o.Driver)
                .Include(o => o.EpodFile)
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
                    driverName = o.Driver != null ? o.Driver.FullName : null,
                    status = o.Status.ToString(),

                    o.IsCompletedByAdmin,
                    o.CompletedUtc,

                    o.IsInvoiced,
                    o.InvoicedUtc,
                    o.IsArchived,
                    o.ArchivedUtc,

                    o.IsPaid,
                    o.PaidUtc,

                    o.ContractorName,
                    o.PaymentDueDate,

                    epodRelPath = o.EpodFile != null ? o.EpodFile.PdfRelativePath : null,
                    invoiceRelPath = o.InvoicePdfRelativePath
                })
                .ToListAsync();

            var ids = orders.Select(o => o.Id).ToList();
            var problemInfo = await LoadProblemInfoForOrdersAsync(ids);

            var baseUrl = BaseUrl();

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
                    o.driverName,
                    o.status,

                    o.IsCompletedByAdmin,
                    o.CompletedUtc,

                    o.IsInvoiced,
                    o.InvoicedUtc,
                    o.IsArchived,
                    o.ArchivedUtc,

                    o.IsPaid,
                    o.PaidUtc,

                    o.ContractorName,
                    o.PaymentDueDate,

                    epodUrl = string.IsNullOrWhiteSpace(o.epodRelPath) ? null : $"{baseUrl}/files/{o.epodRelPath}",
                    invoiceUrl = string.IsNullOrWhiteSpace(o.invoiceRelPath) ? null : $"{baseUrl}/files/{o.invoiceRelPath}",

                    lastProblemNote = p?.LastProblemNote,
                    lastProblemUtc = p?.LastProblemUtc,
                    problemAtStatus = p?.ProblemAtStatus
                };
            }));
        }

        // ============================
        // GET /api/admin/orders/today
        // ============================
        [HttpGet("today")]
        public async Task<IActionResult> GetToday()
        {
            var today = DateTime.Today;
            var tomorrow = today.AddDays(1);

            var orders = await _db.Orders
                .AsNoTracking()
                .Include(o => o.Driver)
                .Include(o => o.EpodFile)
                .Where(o =>
                    o.PickupTime.HasValue &&
                    o.PickupTime.Value >= today &&
                    o.PickupTime.Value < tomorrow
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
                    driverName = o.Driver != null ? o.Driver.FullName : null,
                    status = o.Status.ToString(),

                    o.IsCompletedByAdmin,
                    o.CompletedUtc,

                    o.IsInvoiced,
                    o.InvoicedUtc,
                    o.IsArchived,
                    o.ArchivedUtc,

                    o.IsPaid,
                    o.PaidUtc,

                    o.ContractorName,
                    o.PaymentDueDate,

                    epodRelPath = o.EpodFile != null ? o.EpodFile.PdfRelativePath : null,
                    invoiceRelPath = o.InvoicePdfRelativePath
                })
                .ToListAsync();

            var ids = orders.Select(o => o.Id).ToList();
            var problemInfo = await LoadProblemInfoForOrdersAsync(ids);

            var baseUrl = BaseUrl();

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
                    o.driverName,
                    o.status,

                    o.IsCompletedByAdmin,
                    o.CompletedUtc,

                    o.IsInvoiced,
                    o.InvoicedUtc,
                    o.IsArchived,
                    o.ArchivedUtc,

                    o.IsPaid,
                    o.PaidUtc,

                    o.ContractorName,
                    o.PaymentDueDate,

                    epodUrl = string.IsNullOrWhiteSpace(o.epodRelPath) ? null : $"{baseUrl}/files/{o.epodRelPath}",
                    invoiceUrl = string.IsNullOrWhiteSpace(o.invoiceRelPath) ? null : $"{baseUrl}/files/{o.invoiceRelPath}",

                    lastProblemNote = p?.LastProblemNote,
                    lastProblemUtc = p?.LastProblemUtc,
                    problemAtStatus = p?.ProblemAtStatus
                };
            }));
        }

        // ============================
        // CREATE ORDER (ADMIN)
        // POST /api/admin/orders
        // ============================

        public class CreateOrderRequest
        {
            public string? OrderNumber { get; set; } = null;

            public string PickupAddress { get; set; } = "";
            public string DeliveryAddress { get; set; } = "";
            public DateTime? PickupTime { get; set; }
            public DateTime? DeliveryTime { get; set; }
            public string CargoInfo { get; set; } = "";
            public int? DriverId { get; set; }
            public int? Status { get; set; } // 0..5
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] CreateOrderRequest req)
        {
            var pickupAddress = (req.PickupAddress ?? "").Trim();
            var deliveryAddress = (req.DeliveryAddress ?? "").Trim();

            if (string.IsNullOrWhiteSpace(pickupAddress))
                return BadRequest(new { message = "PickupAddress jest wymagany." });

            if (string.IsNullOrWhiteSpace(deliveryAddress))
                return BadRequest(new { message = "DeliveryAddress jest wymagany." });

            int? driverId = req.DriverId;
            if (driverId.HasValue)
            {
                var driverExists = await _db.Drivers.AnyAsync(d => d.Id == driverId.Value);
                if (!driverExists)
                    return BadRequest(new { message = "Nieprawidłowy kierowca." });
            }

            var status = OrderStatus.Planned;
            if (req.Status.HasValue)
            {
                if (!Enum.IsDefined(typeof(OrderStatus), req.Status.Value))
                    return BadRequest(new { message = "Nieprawidłowy status." });

                status = (OrderStatus)req.Status.Value;
            }

            var orderNumber = await GenerateNextOrderNumberAsync();
            for (int i = 0; i < 5; i++)
            {
                var exists = await _db.Orders.AnyAsync(o => o.OrderNumber == orderNumber);
                if (!exists) break;
                orderNumber = await GenerateNextOrderNumberAsync();
            }

            var order = new Order
            {
                OrderNumber = orderNumber,
                PickupAddress = pickupAddress,
                DeliveryAddress = deliveryAddress,
                PickupTime = req.PickupTime,
                DeliveryTime = req.DeliveryTime,
                CargoInfo = (req.CargoInfo ?? "").Trim(),
                DriverId = driverId,
                Status = status,

                IsCompletedByAdmin = false,
                CompletedUtc = null,
                IsInvoiced = false,
                InvoicedUtc = null,
                IsArchived = false,
                ArchivedUtc = null,

                IsPaid = false,
                PaidUtc = null
            };

            _db.Orders.Add(order);
            await _db.SaveChangesAsync();

            return Ok(new
            {
                message = "Utworzono zlecenie",
                id = order.Id,
                orderNumber = order.OrderNumber
            });
        }

        // ============================
        // ✅ EDIT BASIC FIELDS (ADMIN)
        // PUT /api/admin/orders/{id}/edit
        // Edytuje: pickup/delivery + times
        // ============================

        public class EditOrderRequest
        {
            public string PickupAddress { get; set; } = "";
            public string DeliveryAddress { get; set; } = "";
            public DateTime? PickupTime { get; set; }
            public DateTime? DeliveryTime { get; set; }
        }

        [HttpPut("{id:int}/edit")]
        public async Task<IActionResult> Edit(int id, [FromBody] EditOrderRequest req)
        {
            var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == id);
            if (order == null) return NotFound();

            if (order.IsArchived)
                return BadRequest(new { message = "Nie można edytować zlecenia w archiwum." });

            if (order.IsInvoiced)
                return BadRequest(new { message = "Nie można edytować zlecenia zafakturowanego." });

            var pickupAddress = (req.PickupAddress ?? "").Trim();
            var deliveryAddress = (req.DeliveryAddress ?? "").Trim();

            if (string.IsNullOrWhiteSpace(pickupAddress))
                return BadRequest(new { message = "PickupAddress jest wymagany." });

            if (string.IsNullOrWhiteSpace(deliveryAddress))
                return BadRequest(new { message = "DeliveryAddress jest wymagany." });

            order.PickupAddress = pickupAddress;
            order.DeliveryAddress = deliveryAddress;
            order.PickupTime = req.PickupTime;
            order.DeliveryTime = req.DeliveryTime;

            await _db.SaveChangesAsync();

            return Ok(new { message = "Zapisano zmiany zlecenia." });
        }

        // ============================
        // DELETE ORDER (ADMIN)
        // DELETE /api/admin/orders/{id}
        // ============================

        [HttpDelete("{id:int}")]
        public async Task<IActionResult> Delete(int id)
        {
            var order = await _db.Orders
                .Include(o => o.EpodFile)
                .Include(o => o.StatusLogs)
                .FirstOrDefaultAsync(o => o.Id == id);

            if (order == null) return NotFound();

            if (order.IsArchived)
                return BadRequest(new { message = "Nie można usunąć zlecenia z archiwum. Najpierw wyjmij z archiwum." });

            if (order.IsInvoiced)
                return BadRequest(new { message = "Nie można usunąć zlecenia zafakturowanego." });

            if (order.StatusLogs != null && order.StatusLogs.Count > 0)
            {
                _db.OrderStatusLogs.RemoveRange(order.StatusLogs);
            }

            if (order.EpodFile != null)
            {
                _db.EpodFiles.Remove(order.EpodFile);
            }

            _db.Orders.Remove(order);
            await _db.SaveChangesAsync();

            return Ok(new { message = "Usunięto zlecenie" });
        }

        // ============================
        // PRZYPISANIE / ODPINANIE
        // ============================

        public class AssignDriverRequest
        {
            public int? DriverId { get; set; }
        }

        [HttpPut("{id:int}/assign-driver")]
        public async Task<IActionResult> AssignDriver(int id, [FromBody] AssignDriverRequest req)
        {
            var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == id);
            if (order == null) return NotFound();

            if (req.DriverId.HasValue)
            {
                var driverExists = await _db.Drivers.AnyAsync(d => d.Id == req.DriverId.Value);
                if (!driverExists)
                    return BadRequest(new { message = "Nieprawidłowy kierowca" });

                order.DriverId = req.DriverId.Value;
            }
            else
            {
                order.DriverId = null;
            }

            await _db.SaveChangesAsync();
            return Ok(new { message = "Przypisanie zapisane" });
        }

        // ============================
        // ADMIN: KOREKTA STATUSU
        // ============================

        public class AdminSetStatusRequest
        {
            public int Status { get; set; }
        }

        [HttpPut("{id:int}/status")]
        public async Task<IActionResult> AdminSetStatus(int id, [FromBody] AdminSetStatusRequest req)
        {
            var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == id);
            if (order == null) return NotFound();

            if (!Enum.IsDefined(typeof(OrderStatus), req.Status))
                return BadRequest(new { message = "Nieprawidłowy status" });

            var newStatus = (OrderStatus)req.Status;

            order.Status = newStatus;

            _db.OrderStatusLogs.Add(new OrderStatusLog
            {
                OrderId = order.Id,
                Status = newStatus,
                TimestampUtc = DateTime.UtcNow,
                Lat = null,
                Lng = null,
                ChangedByRole = "Admin",
                ChangedByUserId = UserId(),
                Note = null
            });

            await _db.SaveChangesAsync();
            return Ok(new { message = $"Status ustawiony: {order.Status}" });
        }

        // ============================
        // ZAMKNIĘCIE: tylko z POD
        // ============================

        [HttpPost("{id:int}/complete")]
        public async Task<IActionResult> Complete(int id)
        {
            var order = await _db.Orders
                .Include(o => o.EpodFile)
                .FirstOrDefaultAsync(o => o.Id == id);

            if (order == null) return NotFound();

            if (order.EpodFile == null || string.IsNullOrWhiteSpace(order.EpodFile.PdfRelativePath))
                return BadRequest(new { message = "Nie można zamknąć zlecenia bez POD." });

            order.IsCompletedByAdmin = true;
            order.CompletedUtc = DateTime.UtcNow;

            order.IsArchived = false;
            order.ArchivedUtc = null;

            order.IsPaid = false;
            order.PaidUtc = null;

            await _db.SaveChangesAsync();
            return Ok(new { message = "Zlecenie zamknięte (trafia do Fakturowania)" });
        }

        [HttpPost("{id:int}/reopen")]
        public async Task<IActionResult> Reopen(int id)
        {
            var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == id);
            if (order == null) return NotFound();

            order.IsCompletedByAdmin = false;
            order.CompletedUtc = null;

            order.IsInvoiced = false;
            order.InvoicedUtc = null;
            order.IsArchived = false;
            order.ArchivedUtc = null;

            order.IsPaid = false;
            order.PaidUtc = null;

            order.ContractorName = "";
            order.PaymentDueDate = null;
            order.InvoicePdfRelativePath = null;

            await _db.SaveChangesAsync();
            return Ok(new { message = "Zlecenie przywrócone" });
        }

        // ============================
        // FAKTUROWANIE: dane + upload PDF
        // ============================

        public class InvoiceInfoRequest
        {
            public string ContractorName { get; set; } = "";
            public DateTime? PaymentDueDate { get; set; }
        }

        [HttpPut("{id:int}/invoice-info")]
        public async Task<IActionResult> SaveInvoiceInfo(int id, [FromBody] InvoiceInfoRequest req)
        {
            var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == id);
            if (order == null) return NotFound();

            if (!order.IsCompletedByAdmin)
                return BadRequest(new { message = "Najpierw zamknij zlecenie (z POD)." });

            order.ContractorName = (req.ContractorName ?? "").Trim();
            order.PaymentDueDate = req.PaymentDueDate;

            await _db.SaveChangesAsync();
            return Ok(new { message = "Zapisano dane faktury" });
        }

        public class UploadInvoiceRequest
        {
            public IFormFile File { get; set; } = default!;
        }

        [HttpPost("{id:int}/invoice/upload")]
        [RequestSizeLimit(50_000_000)]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> UploadInvoicePdf(int id, [FromForm] UploadInvoiceRequest req)
        {
            var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == id);
            if (order == null) return NotFound();

            if (!order.IsCompletedByAdmin)
                return BadRequest(new { message = "Najpierw zamknij zlecenie (z POD)." });

            var file = req.File;
            if (file == null || file.Length == 0)
                return BadRequest(new { message = "Brak pliku" });

            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (ext != ".pdf")
                return BadRequest(new { message = "Dozwolony jest tylko PDF" });

            var filesRoot = Path.Combine(_env.ContentRootPath, "files");
            var invoicesDir = Path.Combine(filesRoot, "invoices");
            Directory.CreateDirectory(invoicesDir);

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var safeName = $"invoice_{id}_{stamp}.pdf";
            var absPath = Path.Combine(invoicesDir, safeName);

            await using (var fs = System.IO.File.Create(absPath))
            {
                await file.CopyToAsync(fs);
            }

            order.InvoicePdfRelativePath = Path.Combine("invoices", safeName).Replace("\\", "/");

            await _db.SaveChangesAsync();
            return Ok(new { message = "Faktura PDF zapisana" });
        }

        [HttpPost("{id:int}/invoice")]
        public async Task<IActionResult> MarkInvoiced(int id)
        {
            var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == id);
            if (order == null) return NotFound();

            if (!order.IsCompletedByAdmin)
                return BadRequest(new { message = "Najpierw zamknij zlecenie (z POD)." });

            if (string.IsNullOrWhiteSpace(order.ContractorName))
                return BadRequest(new { message = "Uzupełnij kontrahenta." });

            if (!order.PaymentDueDate.HasValue)
                return BadRequest(new { message = "Uzupełnij termin płatności." });

            if (string.IsNullOrWhiteSpace(order.InvoicePdfRelativePath))
                return BadRequest(new { message = "Dodaj fakturę PDF." });

            order.IsInvoiced = true;
            order.InvoicedUtc = DateTime.UtcNow;

            order.IsPaid = false;
            order.PaidUtc = null;

            await _db.SaveChangesAsync();
            return Ok(new { message = "Oznaczono jako zafakturowane" });
        }

        [HttpPost("{id:int}/archive")]
        public async Task<IActionResult> MoveToArchive(int id)
        {
            var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == id);
            if (order == null) return NotFound();

            if (!order.IsInvoiced)
                return BadRequest(new { message = "Najpierw oznacz jako zafakturowane." });

            if (string.IsNullOrWhiteSpace(order.ContractorName) ||
                !order.PaymentDueDate.HasValue ||
                string.IsNullOrWhiteSpace(order.InvoicePdfRelativePath))
            {
                return BadRequest(new { message = "Brak kompletu danych faktury." });
            }

            order.IsArchived = true;
            order.ArchivedUtc = DateTime.UtcNow;

            await _db.SaveChangesAsync();
            return Ok(new { message = "Przeniesiono do archiwum" });
        }

        [HttpPost("{id:int}/paid")]
        public async Task<IActionResult> MarkPaid(int id)
        {
            var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == id);
            if (order == null) return NotFound();

            if (!order.IsArchived)
                return BadRequest(new { message = "Zapłacone można oznaczać tylko w archiwum." });

            if (!order.IsInvoiced)
                return BadRequest(new { message = "Najpierw oznacz jako zafakturowane." });

            order.IsPaid = true;
            order.PaidUtc = DateTime.UtcNow;

            await _db.SaveChangesAsync();
            return Ok(new { message = "Oznaczono jako zapłacone" });
        }

        [HttpPost("{id:int}/unpaid")]
        public async Task<IActionResult> MarkUnpaid(int id)
        {
            var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == id);
            if (order == null) return NotFound();

            if (!order.IsArchived)
                return BadRequest(new { message = "Cofnięcie płatności dotyczy tylko archiwum." });

            order.IsPaid = false;
            order.PaidUtc = null;

            await _db.SaveChangesAsync();
            return Ok(new { message = "Cofnięto status zapłacone" });
        }

        [HttpPost("{id:int}/unarchive")]
        public async Task<IActionResult> Unarchive(int id)
        {
            var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == id);
            if (order == null) return NotFound();

            if (!order.IsArchived)
                return BadRequest(new { message = "Zlecenie nie jest w archiwum." });

            order.IsArchived = false;
            order.ArchivedUtc = null;

            order.IsInvoiced = false;
            order.InvoicedUtc = null;

            order.IsPaid = false;
            order.PaidUtc = null;

            await _db.SaveChangesAsync();
            return Ok(new { message = "Zlecenie wyjęte z archiwum (cofnięto zafakturowanie)" });
        }
    }
}
