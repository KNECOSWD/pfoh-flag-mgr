using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using PFOH.Api.Data;
using PFOH.Api.Models;
using PFOH.Api.Services;
using Xunit;
using Xunit.Abstractions;

namespace PFOH.Api.Tests;

public class PublicFlagMapLoadBenchTests : IClassFixture<PfohApiFactory>
{
    private readonly PfohApiFactory factory;
    private readonly ITestOutputHelper output;

    public PublicFlagMapLoadBenchTests(PfohApiFactory factory, ITestOutputHelper output)
    {
        this.factory = factory;
        this.output = output;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        db.Database.EnsureCreated();
        if (db.FlagGrids.Any())
        {
            return;
        }

        var category = new ServiceBranchCategory
        {
            ServiceBranchCategoryName = "Bench",
            Description = "bench",
            CreatedBy = "bench",
            ModifiedBy = "bench",
            CreatedDate = DateTime.UtcNow,
            ModifiedDate = DateTime.UtcNow
        };
        db.ServiceBranchCategories.Add(category);
        db.SaveChanges();

        var army = new ServiceBranch
        {
            ServiceBranchCategoryId = category.Id,
            ServiceBranchName = "Army",
            Description = "bench",
            LogoFileName = "Army.png",
            CreatedBy = "bench",
            ModifiedBy = "bench",
            CreatedDate = DateTime.UtcNow,
            ModifiedDate = DateTime.UtcNow
        };
        db.ServiceBranches.Add(army);
        db.SaveChanges();

        var labels = Enumerable.Range(0, 28)
            .Select(index => index < 26 ? ((char)('A' + index)).ToString() : "A" + (char)('A' + index - 26))
            .ToList();
        var grids = new List<FlagGrid>();
        foreach (var label in labels)
        {
            for (var seat = 1; seat <= 40; seat++)
            {
                grids.Add(new FlagGrid
                {
                    FlagGridName = $"{label}-{seat:00}",
                    CreatedBy = "bench",
                    ModifiedBy = "bench",
                    CreatedDate = DateTime.UtcNow,
                    ModifiedDate = DateTime.UtcNow
                });
            }
        }

        db.FlagGrids.AddRange(grids);
        db.SaveChanges();

        var occupied = grids.Take(1000).Select((grid, index) => new Honoree
        {
            FirstName = "Bench",
            MiddleName = "Middle",
            LastName = $"Person{index:0000}",
            Suffix = "Jr",
            Nickname = $"Nick{index:0000}",
            Rank = "Sergeant",
            Description = "Bench bio",
            IsActive = true,
            FlagGridId = grid.Id,
            ServiceBranchId = army.Id,
            ServiceBranchCategoryId = category.Id,
            CreatedBy = "bench",
            ModifiedBy = "bench",
            CreatedDate = DateTime.UtcNow,
            ModifiedDate = DateTime.UtcNow
        }).ToList();
        db.Honorees.AddRange(occupied);
        db.SaveChanges();
    }

    [Fact]
    public async Task Measure_section_load()
    {
        var client = factory.CreateClient();
        var single = await Time(async () =>
        {
            var response = await client.GetAsync("/api/public/flag-map/sections/A");
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadAsByteArrayAsync()).Length;
        });
        output.WriteLine($"single section {single.Ms:0} ms {single.Bytes} bytes");

        var six = await TimeParallel(client, 6);
        output.WriteLine($"6 parallel each {six.Ms:0} ms");

        var all = await TimeParallel(client, 28);
        output.WriteLine($"28 parallel each {all.Ms:0} ms");

        var index = await client.GetAsync("/api/public/flag-map/search-index");
        index.EnsureSuccessStatusCode();
        var indexBytes = (await index.Content.ReadAsByteArrayAsync()).Length;
        output.WriteLine($"search-index {indexBytes} bytes");
        Assert.True(indexBytes > 1000);
        var cache = factory.Services.GetRequiredService<PublicFlagMapCache>();
        Assert.Equal(1, cache.Builds);
    }

    private async Task<(double Ms, int Bytes)> Time(Func<Task<int>> action)
    {
        var watch = Stopwatch.StartNew();
        var bytes = await action();
        watch.Stop();
        return (watch.Elapsed.TotalMilliseconds, bytes);
    }

    private static async Task<(double Ms, int Bytes)> TimeParallel(HttpClient client, int count)
    {
        var labels = Enumerable.Range(0, count)
            .Select(index => index < 26 ? ((char)('A' + index)).ToString() : "A" + (char)('A' + index - 26))
            .ToList();
        var watch = Stopwatch.StartNew();
        var sizes = await Task.WhenAll(labels.Select(async label =>
        {
            var response = await client.GetAsync($"/api/public/flag-map/sections/{label}");
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadAsByteArrayAsync()).Length;
        }));
        watch.Stop();
        return (watch.Elapsed.TotalMilliseconds, sizes.Sum());
    }
}
