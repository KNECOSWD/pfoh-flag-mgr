using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PFOH.Snapshot;
using PFOH.Snapshot.Function;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        var configuration = context.Configuration;
        var required = new[]
        {
            "Snapshot:SqlConnectionString",
            "Snapshot:KeyVaultUri",
            "Snapshot:ManagedIdentityClientId",
            "Snapshot:StorageAccountName"
        };
        var missing = required.Where(key => string.IsNullOrWhiteSpace(configuration[key])).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException("Missing snapshot settings: " + string.Join(", ", missing));
        }

        services.AddHostedService<SnapshotGraphStartupCheck>();
        services.AddOptions<SnapshotScheduleOptions>()
            .Bind(configuration.GetSection(SnapshotScheduleOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<SnapshotScheduleOptions>, SnapshotScheduleOptionsValidator>();

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
        var sqlConnectionString = configuration["Snapshot:SqlConnectionString"]!;
        services.AddSingleton<IUserTableSource>(_ => new SqlUserTableSource(sqlConnectionString));
        var graphMissing = SnapshotGraphConfig.Missing(key => configuration[key]);
        if (graphMissing.Count == 0)
        {
            var graphCredentials = GraphCertificateCredentialSource.Create(
                new Uri(configuration["Snapshot:KeyVaultUri"]!),
                credential,
                configuration["Snapshot:GraphCertificateName"]!,
                configuration["Snapshot:GraphTenantId"]!,
                configuration["Snapshot:GraphClientId"]!);
            services.AddSingleton<IDirectorySource>(_ => new GraphDirectorySource(graphCredentials, http));
        }
        else
        {
            services.AddSingleton<IDirectorySource>(_ => new IncompleteGraphDirectorySource());
        }
        services.AddSingleton<ISnapshotSink>(_ => new BlobSnapshotSink(container));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<SnapshotJob>();
    })
    .Build();

host.Run();
