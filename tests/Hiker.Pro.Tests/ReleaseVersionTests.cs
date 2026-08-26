using Hiker.Pro.Release;
using Hiker.Pro.State;
using Xunit;

namespace Hiker.Pro.Tests;

public sealed class ReleaseVersionTests
{
    [Fact]
    public async Task CompiledVersionFacts_MatchRepositoryMirrorsAndStateSchema()
    {
        var root = FindRepositoryRoot();
        var facts = BuildVersionFacts.Current;
        Assert.Equal(facts.Product, (await File.ReadAllTextAsync(Path.Combine(root, "VERSION"))).Trim());
        using var document = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "release-versions.json")));
        Assert.Equal(facts.Product, document.RootElement.GetProperty("product").GetString());
        Assert.Equal(facts.Runtime, document.RootElement.GetProperty("runtime").GetString());
        Assert.Equal(facts.StateSchema, document.RootElement.GetProperty("state_schema").GetString());

        await using var project = await TestProject.CreateAsync();
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={project.Layout.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM metadata WHERE key='schema_version';";
        Assert.Equal(facts.StateSchema, await command.ExecuteScalarAsync());
    }

    [Fact]
    public void ProductionDomainWrites_AreRoutedThroughStateCoordinator()
    {
        var runtime = Path.Combine(FindRepositoryRoot(), "src", "Hiker.Pro.Runtime");
        var stateDirectory = Path.Combine(runtime, "State") + Path.DirectorySeparatorChar;
        var directWritePrimitives = new[]
        {
            ".CompareAndSwapAsync(",
            ".CompareAndSwapBatchAsync(",
            ".AppendEventAsync(",
            ".InvalidateAsync(",
            ".MaterializeAsync(",
            ".MigrateAsync(",
            ".RepairAsync(",
            ".GarbageCollectAsync(",
            ".RegisterArtifactAsync("
        };
        var bypasses = Directory.EnumerateFiles(runtime, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.StartsWith(stateDirectory, StringComparison.OrdinalIgnoreCase))
            .Select(path => new { Path = path, Content = File.ReadAllText(path) })
            .Where(item => item.Content.Contains("IStateStore", StringComparison.Ordinal) &&
                           directWritePrimitives.Any(item.Content.Contains))
            .Select(item => Path.GetRelativePath(runtime, item.Path))
            .ToArray();

        Assert.Empty(bypasses);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "release-versions.json")) && Directory.Exists(Path.Combine(directory.FullName, ".git")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found from the test output");
    }
}
