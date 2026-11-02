namespace PFOH.Api.Services;

public sealed record StoredPdf(byte[] Bytes, string? Stamp, string? ETag);
