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

        // Admin + Driver: SAS do uploadu (driver tylko dla swoich zleceń)
        [Authorize(Roles = "Admin,Driver,admin,driver")]
        [HttpPost("{orderId:int}/upload-sas")]
        public async Task<ActionResult<UploadSasResponse>> GetUploadSas(int orderId)
        {
            var order = await _db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId);
            if (order is null)
                return NotFound("Order not found.");

            if (IsDriver && order.IsCompletedByAdmin)
                return Forbid("Order is completed and locked by admin.");

            if (IsDriver && !IsAdmin)
            {
                var driverId = await GetDriverIdForCurrentUserAsync();
                if (driverId is null)
                    return Forbid("Driver profile not found.");

                var isMyOrder = await DriverOwnsOrderAsync(order);
                if (!isMyOrder)
                    return Forbid("Driver cannot upload ePOD for someone else's order.");
            }

            // ✅ docelowy blobName (zawsze w kontenerze epod)
            var blobName = $"orders/{orderId}/epod_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.pdf";

            // ✅ UPSERT metadanych do SQL
            var epod = await _db.EpodFiles.FirstOrDefaultAsync(e => e.OrderId == orderId);

            if (epod == null)
            {
                epod = new EpodFile
                {
                    OrderId = orderId,
                    BlobName = blobName,
                    CreatedUtc = DateTime.UtcNow
                };
                _db.EpodFiles.Add(epod);
            }
            else
            {
                epod.BlobName = blobName;
                epod.CreatedUtc = DateTime.UtcNow;
                _db.EpodFiles.Update(epod);
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

            if (IsDriver && order.IsCompletedByAdmin)
                return Forbid("Order is completed and locked by admin.");

            // ✅ Guard: blob musi istnieć
            if (!await _blob.BlobExistsAsync(req.BlobName))
                return BadRequest("Blob does not exist.");

            var existing = await _db.EpodFiles.FirstOrDefaultAsync(e => e.OrderId == orderId);

            if (IsDriver && !IsAdmin)
            {
                var driverId = await GetDriverIdForCurrentUserAsync();
                if (driverId is null)
                    return Forbid("Driver profile not found.");

                var isMyOrder = await DriverOwnsOrderAsync(order);
                if (!isMyOrder)
                    return Forbid("Driver cannot add ePOD for someone else's order.");

                if (existing is not null)
                    return Forbid("Driver cannot edit or overwrite existing ePOD.");

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
                // Admin overwrite
                existing.BlobName = req.BlobName;
                existing.Lat = req.Lat;
                existing.Lng = req.Lng;
                existing.CreatedUtc = DateTime.UtcNow;
            }

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

            if (IsDriver && order.IsCompletedByAdmin)
                return Forbid("Order is completed and locked by admin.");

            var existing = await _db.EpodFiles.FirstOrDefaultAsync(e => e.OrderId == orderId);

            if (IsDriver && !IsAdmin)
            {
                var driverId = await GetDriverIdForCurrentUserAsync();
                if (driverId is null)
                    return Forbid("Driver profile not found.");

                var isMyOrder = await DriverOwnsOrderAsync(order);
                if (!isMyOrder)
                    return Forbid("Driver cannot add ePOD for someone else's order.");

                if (existing is not null)
                    return Forbid("Driver cannot edit or overwrite existing ePOD.");

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
                    Lng = lng
                };
                _db.EpodFiles.Add(existing);
            }
            else
            {
                // Admin overwrite
                existing.BlobName = blobName;
                existing.CreatedUtc = DateTime.UtcNow;
                existing.Lat = lat;
                existing.Lng = lng;
            }

            await _db.SaveChangesAsync();
            return Ok(new { blobName });
        }
    }
}
