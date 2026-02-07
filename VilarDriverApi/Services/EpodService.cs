using Microsoft.AspNetCore.Http;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;

namespace VilarDriverApi.Services
{
    public class EpodService
    {
        private readonly string _storageRoot;

        public EpodService(IWebHostEnvironment env)
        {
            // Trzymamy pliki w: <projekt>/Storage
            _storageRoot = Path.Combine(env.ContentRootPath, "Storage");

            Directory.CreateDirectory(_storageRoot);
            Directory.CreateDirectory(Path.Combine(_storageRoot, "epod"));
            Directory.CreateDirectory(Path.Combine(_storageRoot, "tmp"));
        }
        public string GetAbsolutePath(string relPath)
        {
            var safeRel = relPath.Replace("/", Path.DirectorySeparatorChar.ToString());
            return Path.Combine(_storageRoot, safeRel);
        }

        /// <summary>
        /// Zapisuje gotowy PDF (bez konwersji) do Storage/epod i zwraca ścieżkę względną (np. "epod/epod_1_20260116_203000.pdf")
        /// </summary>
        public async Task<string> SavePdfAsync(int orderId, IFormFile file)
        {
            if (file == null || file.Length == 0)
                throw new ArgumentException("Brak pliku PDF", nameof(file));

            var ext = Path.GetExtension(file.FileName);
            if (string.IsNullOrWhiteSpace(ext) || !ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                ext = ".pdf";

            var fileName = $"epod_{orderId}_{DateTime.UtcNow:yyyyMMdd_HHmmss}{ext}";
            var relPath = Path.Combine("epod", fileName).Replace("\\", "/");

            var absPath = Path.Combine(_storageRoot, "epod", fileName);

            await using var fs = new FileStream(absPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await file.CopyToAsync(fs);

            return relPath;
        }

        /// <summary>
        /// Buduje PDF z listy zdjęć. Każde zdjęcie trafia na osobną stronę.
        /// Zwraca ścieżkę względną do PDF w Storage.
        /// </summary>
        public async Task<string> BuildPdfFromPhotosAsync(int orderId, List<IFormFile> photos)
        {
            if (photos == null || photos.Count == 0)
                throw new ArgumentException("Brak zdjęć", nameof(photos));

            // 1) zapisz zdjęcia tymczasowo (PdfSharpCore lubi ścieżki plików)
            var tmpDir = Path.Combine(_storageRoot, "tmp", $"order_{orderId}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tmpDir);

            var imagePaths = new List<string>();

            try
            {
                var idx = 0;
                foreach (var p in photos)
                {
                    idx++;

                    var ext = Path.GetExtension(p.FileName);
                    if (string.IsNullOrWhiteSpace(ext)) ext = ".jpg";

                    var imgName = $"img_{idx}{ext}";
                    var imgAbs = Path.Combine(tmpDir, imgName);

                    await using (var fs = new FileStream(imgAbs, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await p.CopyToAsync(fs);
                    }

                    imagePaths.Add(imgAbs);
                }

                // 2) zbuduj PDF
                var pdf = new PdfDocument();

                foreach (var path in imagePaths)
                {
                    using var img = XImage.FromFile(path);

                    var page = pdf.AddPage();

                    // dopasuj stronę do obrazu (prosto i skutecznie)
                    page.Width = img.PixelWidth;
                    page.Height = img.PixelHeight;

                    using var gfx = XGraphics.FromPdfPage(page);
                    gfx.DrawImage(img, 0, 0, page.Width, page.Height);
                }

                var pdfName = $"epod_{orderId}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.pdf";
                var relPath = Path.Combine("epod", pdfName).Replace("\\", "/");
                var absPdfPath = Path.Combine(_storageRoot, "epod", pdfName);

                using (var fs = new FileStream(absPdfPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    pdf.Save(fs);
                }

                return relPath;
            }
            finally
            {
                // sprzątanie tmp
                try { Directory.Delete(tmpDir, true); } catch { /* ignore */ }
            }
        }
    }
}