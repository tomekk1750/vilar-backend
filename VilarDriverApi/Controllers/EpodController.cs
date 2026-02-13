using System;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VilarDriverApi.Data;
using VilarDriverApi.Services;
using VilarDriverApi.Models;

namespace VilarDriverApi.Controllers
{
    [ApiController]
    [Route("api/epod")]
    public class EpodController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly BlobStorageService _blob;
        private readonly EpodService _epod;

        public EpodController(AppDbContext db, BlobStorageService blob, EpodService epod)
        {
            _db = db;
            _blob = blob;
            _epod = epod;
        }

        private bool IsAdmin => User.IsInRole("Admin") || User.IsInRole("admin");
        private bool IsDriver => User.IsInRole("Driver") || User.IsInRole("driver");

        private bool TryGetUserId(out int userId)
        {
            var userIdStr = User.FindFirst("sub")?.Value; // NameClaimType="sub"
            return int.TryParse(userIdStr, out userId);
        }

        private async Task<int?> GetDriverIdForCurrentUserAsync()
        {
            if (!TryGetUserId(out var userId))
                return null;

            return await _db.Drivers
                .Where(d => d.UserId == userId)
                .Select(d => (int?)d.Id)
                .FirstOrDefaultAsync();
        }

        private async Task<bool> DriverOwnsOrderAsync(Order order)
        {
            var driverId = await GetDriverIdForCurrentUserAsync();
            if (driverId is null)
                return false;

            if (order.DriverId is null)
                return false;

            return order.DriverId.Value == driverId.Value;
        }

        public record UploadSasResponse(string BlobName, string UploadUrl);

        [Authorize(Roles = "Admin,Driver,admin,driver")]
        [HttpPost("{orderId:int}/upload-sas")]
        public async Task<ActionResult<UploadSasResponse>> GetUploadSas(int orderId)
        {
            var order = await _db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId);
            if (order is null)
                return NotFound("Order not found.");

            // FIX: nie używaj Forbid("...") - string jest traktowany jako scheme i kończy się 500
            if (IsDriver && order.IsCompletedByAdmin)
                return StatusCode(StatusCodes.Status403Forbidden, new
                {
                    code = "ORDER_LOCKED",
                    message = "Order is completed and locked by admin."
                });

            if (IsDriver && !IsAdmin)
            {
                var driverId = await GetDriverIdForCurrentUserAsync();
                if (driverId is null)
                    return StatusCode(StatusCodes.Status403Forbidden, new
                    {
                        code = "DRIVER_PROFILE_NOT_FOUND",
                        message = "Driver profile not found."
                    });

                var isMyOrder = await DriverOwnsOrderAsync(order);
                if (!isMyOrder)
                    return StatusCode(StatusCodes.Status403Forbidden, new
                    {
                        code = "NOT_YOUR_ORDER",
                        message = "Driver cannot upload ePOD for someone else's order."
                    });
            }

            var blobName = $"orders/{orderId}/epod_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.pdf";

            var epod = await _db.EpodFiles.FirstOrDefaultAsync(e => e.OrderId == orderId);

            if (epod == null)
            {
                epod = new EpodFile
                {
                    OrderId = orderId,
                    BlobName = blobName,
                    CreatedUtc = DateTime.UtcNow,

                    Status = 0,          // Pending
                    UploadedUtc = null,
                    ConfirmedUtc = null
                };

                _db.EpodFiles.Add(epod);
            }
            else
            {
                epod.BlobName = blobName;
                epod.CreatedUtc = DateTime.UtcNow;

                epod.Status = 0;        // Pending
                epod.UploadedUtc = null;
                epod.ConfirmedUtc = null
;
            }

            await _db.SaveChangesAsync();

            var sasUri = _blob.CreateUploadSas(blobName, "application/pdf", TimeSpan.FromMinutes(10));
            return Ok(new UploadSasResponse(blobName, sasUri.ToString()));
        }

        public record AttachRequest(string BlobName, double? Lat, double? Lng);

        // Admin: create + overwrite
        // Driver: tylko create, tylko raz, tylko Delivered, tylko swoje zlecenia
        [Authorize(Roles = "Admin,Driver,admin,driver")]
        [HttpPost("{orderId:int}/attach")]
        public async Task<IActionResult> Attach(int orderId, [FromBody] AttachRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.BlobName))
                return BadRequest("BlobName is required.");

            var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == orderId);
            if (order is null)
                return NotFound("Order not found.");

            // FIX: nie używaj Forbid("...") - string jest traktowany jako scheme i kończy się 500
            if (IsDriver && order.IsCompletedByAdmin)
                return StatusCode(StatusCodes.Status403Forbidden, new
                {
                    code = "ORDER_LOCKED",
                    message = "Order is completed and locked by admin."
                });

            // ✅ Guard: blob musi istnieć
            if (!await _blob.BlobExistsAsync(req.BlobName))
                return BadRequest("Blob does not exist.");

            var existing = await _db.EpodFiles.FirstOrDefaultAsync(e => e.OrderId == orderId);

            if (IsDriver && !IsAdmin)
            {
                var driverId = await GetDriverIdForCurrentUserAsync();
                if (driverId is null)
                    return StatusCode(StatusCodes.Status403Forbidden, new
                    {
                        code = "DRIVER_PROFILE_NOT_FOUND",
                        message = "Driver profile not found."
                    });

                var isMyOrder = await DriverOwnsOrderAsync(order);
                if (!isMyOrder)
                    return StatusCode(StatusCodes.Status403Forbidden, new
                    {
                        code = "NOT_YOUR_ORDER",
                        message = "Driver cannot add ePOD for someone else's order."
                    });

                // ✅ Driver: blokuj tylko FINALNY ePOD (Pending jest OK, bo upload-sas tworzy rekord wcześniej)
                if (existing is not null && (existing.ConfirmedUtc != null || existing.Status != 0))
                    return Conflict(new
                    {
                        code = "EPOD_ALREADY_EXISTS",
                        message = "Driver cannot edit or overwrite existing ePOD."
                    });

                // ✅ Jeśli rekord Pending istnieje, Driver może tylko “dopiąć” ten sam blobName
                if (existing is not null && existing.Status == 0 &&
                    !string.Equals(existing.BlobName, req.BlobName, StringComparison.Ordinal))
                {
                    return BadRequest(new
                    {
                        code = "EPOD_BLOBNAME_MISMATCH",
                        message = "BlobName mismatch."
                    });
                }

                if (order.Status != OrderStatus.Delivered)
                    return StatusCode(StatusCodes.Status409Conflict,
                        "ePOD can be added only when order status is Delivered.");
            }

            if (existing is null)
            {
                existing = new EpodFile
                {
                    OrderId = orderId,
                    BlobName = req.BlobName,
                    CreatedUtc = DateTime.UtcNow,
                    Lat = req.Lat,
                    Lng = req.Lng
                };
                _db.EpodFiles.Add(existing);
            }
            else
            {
                // Admin overwrite OR driver finalizing pending record
                existing.BlobName = req.BlobName;
                existing.Lat = req.Lat;
                existing.Lng = req.Lng;
                existing.CreatedUtc = DateTime.UtcNow;
            }

            // ✅ zaznacz, że attach po uploadzie doszedł
            existing.UploadedUtc = DateTime.UtcNow;
            existing.Status = 0; // nadal Pending do czasu Confirm

            await _db.SaveChangesAsync();
            return Ok();
        }

        public record DownloadSasResponse(string DownloadUrl);

        // Tylko admin pobiera
        [Authorize(Roles = "Admin,admin")]
        [HttpGet("{orderId:int}/download-sas")]
        public async Task<ActionResult<DownloadSasResponse>> GetDownloadSas(int orderId, CancellationToken ct)
        {
            var epod = await _db.EpodFiles.AsNoTracking().FirstOrDefaultAsync(e => e.OrderId == orderId, ct);
            if (epod is null || string.IsNullOrWhiteSpace(epod.BlobName))
                return NotFound(new { code = "EPOD_NOT_FOUND" });

            // ✅ KLUCZ: sprawdź czy blob faktycznie istnieje (legacy DB / usunięty plik)
            var exists = await _blob.BlobExistsAsync(epod.BlobName, ct);
            if (!exists)
                return NotFound(new { code = "EPOD_NOT_FOUND" });

            var sasUri = _blob.CreateDownloadSas(epod.BlobName, TimeSpan.FromMinutes(5));
            return Ok(new DownloadSasResponse(sasUri.ToString()));
        }

        public record ConfirmEpodResponse(bool Exists, string? BlobName, int Status, DateTime? UploadedUtc, DateTime? ConfirmedUtc);

        [Authorize(Roles = "Admin,admin")]
        [HttpPost("{orderId:int}/confirm")]
        public async Task<ActionResult<ConfirmEpodResponse>> ConfirmEpod(int orderId)
        {
            var epod = await _db.EpodFiles.FirstOrDefaultAsync(e => e.OrderId == orderId);

            if (epod is null || string.IsNullOrWhiteSpace(epod.BlobName))
            {
                return Conflict(new
                {
                    code = "EPOD_MISSING",
                    message = "Brak ePOD w bazie dla tego zamówienia."
                });
            }

            var blobName = _blob.NormalizeBlobName(epod.BlobName);

            // idempotent
            if (epod.Status == 1 && epod.ConfirmedUtc != null)
                return Ok(new ConfirmEpodResponse(true, blobName, epod.Status, epod.UploadedUtc, epod.ConfirmedUtc));

            var exists = await _blob.BlobExistsAsync(blobName);

            if (!exists)
            {
                epod.Status = 2; // Failed (opcjonalnie)
                await _db.SaveChangesAsync();

                return Conflict(new
                {
                    code = "EPOD_BLOB_NOT_FOUND",
                    message = "Blob nie istnieje w storage.",
                    blobName
                });
            }

            epod.BlobName = blobName;
            epod.Status = 1;
            epod.UploadedUtc = DateTime.UtcNow;     // ✅ KLUCZOWE
            epod.ConfirmedUtc = DateTime.UtcNow;

            await _db.SaveChangesAsync();

            return Ok(new ConfirmEpodResponse(true, blobName, epod.Status, epod.UploadedUtc, epod.ConfirmedUtc));
        }

        // Zdjęcia -> PDF -> Blob -> DB (tak jak attach), z tymi samymi zasadami security
        [Authorize(Roles = "Admin,Driver,admin,driver")]
        [HttpPost("{orderId:int}/from-photos")]
        [RequestSizeLimit(50_000_000)]
        public async Task<IActionResult> CreatePdfFromPhotosAndUpload(
            int orderId,
            [FromForm] List<IFormFile> photos,
            [FromForm] double? lat,
            [FromForm] double? lng)
        {
            var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == orderId);
            if (order is null)
                return NotFound("Order not found.");

            // FIX: nie używaj Forbid("...") - string jest traktowany jako scheme i kończy się 500
            if (IsDriver && order.IsCompletedByAdmin)
                return StatusCode(StatusCodes.Status403Forbidden, new
                {
                    code = "ORDER_LOCKED",
                    message = "Order is completed and locked by admin."
                });

            var existing = await _db.EpodFiles.FirstOrDefaultAsync(e => e.OrderId == orderId);

            if (IsDriver && !IsAdmin)
            {
                var driverId = await GetDriverIdForCurrentUserAsync();
                if (driverId is null)
                    return StatusCode(StatusCodes.Status403Forbidden, new
                    {
                        code = "DRIVER_PROFILE_NOT_FOUND",
                        message = "Driver profile not found."
                    });

                var isMyOrder = await DriverOwnsOrderAsync(order);
                if (!isMyOrder)
                    return StatusCode(StatusCodes.Status403Forbidden, new
                    {
                        code = "NOT_YOUR_ORDER",
                        message = "Driver cannot add ePOD for someone else's order."
                    });

                // ✅ Driver: blokuj tylko FINALNY ePOD (Pending jest OK)
                if (existing is not null && (existing.ConfirmedUtc != null || existing.Status != 0))
                    return Conflict(new
                    {
                        code = "EPOD_ALREADY_EXISTS",
                        message = "Driver cannot edit or overwrite existing ePOD."
                    });

                if (order.Status != OrderStatus.Delivered)
                    return StatusCode(StatusCodes.Status409Conflict,
                        "ePOD can be added only when order status is Delivered.");
            }

            if (photos == null || photos.Count == 0)
                return BadRequest("No photos provided.");

            // 1) Zbuduj PDF lokalnie (tymczasowo)
            var relPdfPath = await _epod.BuildPdfFromPhotosAsync(orderId, photos);
            var absPdfPath = _epod.GetAbsolutePath(relPdfPath);

            // 2) Upload do Blob
            var blobName = $"orders/{orderId}/epod_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.pdf";
            await using (var fs = System.IO.File.OpenRead(absPdfPath))
            {
                await _blob.UploadAsync(blobName, fs, "application/pdf");
            }

            // 3) Usuń lokalny plik
            try { System.IO.File.Delete(absPdfPath); } catch { /* ignore */ }

            // 4) Zapis do DB
            if (existing is null)
            {
                existing = new EpodFile
                {
                    OrderId = orderId,
                    BlobName = blobName,
                    CreatedUtc = DateTime.UtcNow,
                    Lat = lat,
                    Lng = lng,
                    Status = 0,
                    UploadedUtc = DateTime.UtcNow
                };
                _db.EpodFiles.Add(existing);
            }
            else
            {
                // Admin overwrite OR driver overwriting pending draft
                existing.BlobName = blobName;
                existing.CreatedUtc = DateTime.UtcNow;
                existing.Lat = lat;
                existing.Lng = lng;
                existing.Status = 0;
                existing.UploadedUtc = DateTime.UtcNow;
            }

            await _db.SaveChangesAsync();
            return Ok(new { blobName });
        }
    }
}
