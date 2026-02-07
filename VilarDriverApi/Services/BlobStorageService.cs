using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;

namespace VilarDriverApi.Services
{
    public class BlobStorageService
    {
        private readonly string _connString;
        private readonly string _containerName;

        public BlobStorageService(IConfiguration config)
        {
            _connString = config["AzureStorage:ConnectionString"]
                ?? throw new InvalidOperationException("Missing AzureStorage:ConnectionString (AzureStorage__ConnectionString).");

            _containerName = config["AzureStorage:ContainerName"] ?? "epod";
        }

        private BlobContainerClient GetContainer()
        {
            var container = new BlobContainerClient(_connString, _containerName);
            container.CreateIfNotExists();
            return container;
        }

        public Uri CreateUploadSas(string blobName, string contentType, TimeSpan ttl)
        {
            var container = GetContainer();
            var blobClient = container.GetBlobClient(blobName);

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

            // Upload only: create + write
            sasBuilder.SetPermissions(BlobSasPermissions.Create | BlobSasPermissions.Write);

            var sas = sasBuilder.ToSasQueryParameters(credential).ToString();
            return new UriBuilder(blobClient.Uri) { Query = sas }.Uri;
        }

        public Uri CreateDownloadSas(string blobName, TimeSpan ttl)
        {
            var container = GetContainer();
            var blobClient = container.GetBlobClient(blobName);

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

            // Download only: read
            sasBuilder.SetPermissions(BlobSasPermissions.Read);

            var sas = sasBuilder.ToSasQueryParameters(credential).ToString();
            return new UriBuilder(blobClient.Uri) { Query = sas }.Uri;
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
