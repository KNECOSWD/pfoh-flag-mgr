using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace PFOH.Snapshot;

public sealed class BlobSnapshotSink(BlobContainerClient container) : ISnapshotSink
{
    public async Task WriteAsync(string blobName, byte[] utf8Json, CancellationToken ct)
    {
        var blob = container.GetBlobClient(blobName);
        await using var stream = new MemoryStream(utf8Json, writable: false);
        await blob.UploadAsync(
            stream,
            new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json; charset=utf-8" }
            },
            ct);
    }
}
