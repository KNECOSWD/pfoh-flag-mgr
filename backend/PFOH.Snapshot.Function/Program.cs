using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PFOH.Snapshot;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        var configuration = context.Configuration;
        var required = new[]
        {
            "Snapshot:SqlConnectionString",
            "Snapshot:GraphTenantId",
            "Snapshot:GraphClientId",
            "Snapshot:GraphCertificateName",
            "Snapshot:KeyVaultUri",
            "Snapshot:ManagedIdentityClientId",
            "Snapshot:StorageAccountName"
        };
        var missing = required.Where(key => string.IsNullOrWhiteSpace(configuration[key])).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException("Missing snapshot settings: " + string.Join(", ", missing));
        }

        var identityClientId = configuration["Snapshot:ManagedIdentityClientId"]!;
        var credential = new ManagedIdentityCredential(identityClientId);
        var containerName = configuration["Snapshot:StorageContainer"];
        if (string.IsNullOrWhiteSpace(containerName))
        {
            containerName = "pfoh-reports";
        }

        var accountName = configuration["Snapshot:StorageAccountName"]!;
        var container = new BlobContainerClient(
            new Uri($"https://{accountName}.blob.core.windows.net/{containerName}"),
            credential);
        var http = new HttpClient();
        var graphTenantId = configuration["Snapshot:GraphTenantId"]!;
        var graphClientId = configuration["Snapshot:GraphClientId"]!;
        var graphCredentials = GraphCertificateCredentialSource.Create(
            new Uri(configuration["Snapshot:KeyVaultUri"]!),
            credential,
            configuration["Snapshot:GraphCertificateName"]!,
            graphTenantId,
            graphClientId);
        var sqlConnectionString = configuration["Snapshot:SqlConnectionString"]!;
        services.AddSingleton<IUserTableSource>(_ => new SqlUserTableSource(sqlConnectionString));
        services.AddSingleton<IDirectorySource>(_ => new GraphDirectorySource(graphCredentials, http));
        services.AddSingleton<ISnapshotSink>(_ => new BlobSnapshotSink(container));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<SnapshotJob>();
    })
    .Build();

host.Run();
