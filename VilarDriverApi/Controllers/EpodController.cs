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

        public EpodController(AppDbContext db, BlobStorageService blob)
        {
            _db = db;
            _blob = blob;
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
            // DriverId wyliczamy tylko z DB na podstawie sub(UserId)
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

            var blobName = $"orders/{orderId}/epod_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.pdf";
            var sasUri = _blob.CreateUploadSas(blobName, "application/pdf", TimeSpan.FromMinutes(10));

            return Ok(new UploadSasResponse(blobName, sasUri.ToString()));
        }

        public record AttachRequest(string BlobName, double? Lat, double? Lng);

        // Admin: create + overwrite
        // Driver: tylko create, tylko raz, tylko Delivered, tylko swoje zlecenia
        [Authorize(Roles = "admin,driver")]
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
        public async Task<ActionResult<DownloadSasResponse>> GetDownloadSas(int orderId)
        {
            var epod = await _db.EpodFiles.AsNoTracking().FirstOrDefaultAsync(e => e.OrderId == orderId);
            if (epod is null || string.IsNullOrWhiteSpace(epod.BlobName))
                return NotFound("Epod file not found.");

            var sasUri = _blob.CreateDownloadSas(epod.BlobName, TimeSpan.FromMinutes(5));
            return Ok(new DownloadSasResponse(sasUri.ToString()));
        }
    }
}
