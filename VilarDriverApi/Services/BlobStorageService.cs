using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;

namespace VilarDriverApi.Services
{
    public class BlobStorageService
    {
        private readonly string _connString;
        private readonly string _containerName;
        private readonly BlobContainerClient _container;
        

        public BlobStorageService(IConfiguration config)
        {
            _connString = config["AzureStorage:ConnectionString"]
                ?? throw new InvalidOperationException("Missing AzureStorage:ConnectionString (AzureStorage__ConnectionString).");

            _containerName = config["AzureStorage:ContainerName"] ?? "epod";

            _container = new BlobContainerClient(_connString, _containerName);
            _container.CreateIfNotExists(PublicAccessType.None);
        }

        public async Task UploadAsync(string blobName, Stream content, string contentType)
        {
            if (content is null) throw new ArgumentNullException(nameof(content));
            if (content.CanSeek) content.Position = 0;

            blobName = NormalizeBlobName(blobName);
            var blobClient = _container.GetBlobClient(blobName);

            await blobClient.UploadAsync(content, overwrite: true);

            await blobClient.SetHttpHeadersAsync(new BlobHttpHeaders
            {
                ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/pdf" : contentType
            });
        }

        public async Task PingAsync(CancellationToken ct = default)
        {
            await _container.GetPropertiesAsync(cancellationToken: ct);
        }

        public async Task<bool> BlobExistsAsync(string blobName, CancellationToken ct = default)
        {
            blobName = NormalizeBlobName(blobName);
            var blobClient = _container.GetBlobClient(blobName);
            return await blobClient.ExistsAsync(ct);
        }

        public Uri CreateUploadSas(string blobName, string contentType, TimeSpan ttl)
        {
            blobName = NormalizeBlobName(blobName);
            var blobClient = _container.GetBlobClient(blobName);

            var (accountName, accountKey) = ExtractAccountNameAndKey(_connString);
            var credential = new StorageSharedKeyCredential(accountName, accountKey);

            var sasBuilder = new BlobSasBuilder
            {
                BlobContainerName = _containerName,
                BlobName = blobName,
                Resource = "b",
                StartsOn = DateTimeOffset.UtcNow.AddMinutes(-2),
                ExpiresOn = DateTimeOffset.UtcNow.Add(ttl),
                ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/pdf" : contentType
            };

            sasBuilder.SetPermissions(BlobSasPermissions.Create | BlobSasPermissions.Write);

            var sas = sasBuilder.ToSasQueryParameters(credential).ToString();
            return new UriBuilder(blobClient.Uri) { Query = sas }.Uri;
        }

        public Uri CreateDownloadSas(string blobName, TimeSpan ttl)
        {
            blobName = NormalizeBlobName(blobName);
            var blobClient = _container.GetBlobClient(blobName);

            var (accountName, accountKey) = ExtractAccountNameAndKey(_connString);
            var credential = new StorageSharedKeyCredential(accountName, accountKey);

            var sasBuilder = new BlobSasBuilder
            {
                BlobContainerName = _containerName,
                BlobName = blobName,
                Resource = "b",
                StartsOn = DateTimeOffset.UtcNow.AddMinutes(-2),
                ExpiresOn = DateTimeOffset.UtcNow.Add(ttl)
            };

            sasBuilder.SetPermissions(BlobSasPermissions.Read);

            var sas = sasBuilder.ToSasQueryParameters(credential).ToString();
            return new UriBuilder(blobClient.Uri) { Query = sas }.Uri;
        }

        public string NormalizeBlobName(string blobName)
        {
            if (string.IsNullOrWhiteSpace(blobName))
                throw new ArgumentException("blobName is required", nameof(blobName));

            var name = blobName.Trim().TrimStart('/').Replace("\\", "/");

            // Jeśli ktoś zapisze "epod/..." a _containerName == "epod",
            // to usuwamy ten prefix, bo kontener już to reprezentuje.
            var prefix = _containerName.Trim().Trim('/');
            if (!string.IsNullOrWhiteSpace(prefix))
            {
                var p = prefix + "/";
                if (name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                    name = name.Substring(p.Length);
            }

            return name;
        }

        private static (string accountName, string accountKey) ExtractAccountNameAndKey(string connString)
        {
            var parts = connString.Split(';', StringSplitOptions.RemoveEmptyEntries);
            string? name = null, key = null;

            foreach (var p in parts)
            {
                var kv = p.Split('=', 2);
                if (kv.Length != 2) continue;

                var k = kv[0].Trim();
                var v = kv[1].Trim();

                if (k.Equals("AccountName", StringComparison.OrdinalIgnoreCase)) name = v;
                if (k.Equals("AccountKey", StringComparison.OrdinalIgnoreCase)) key = v;
            }

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(key))
                throw new InvalidOperationException("Cannot parse AccountName/AccountKey from Azure Storage connection string.");

            return (name!, key!);
        }
    }
}
