using Hiker.Pro.State;

namespace Hiker.Pro.Tests;

internal sealed class TestProject : IAsyncDisposable
{
    private TestProject(string root, SqliteStateStore store)
    {
        Root = root;
        Layout = new ProjectLayout(root);
        Store = store;
        Coordinator = new StateCoordinator(store);
        Bootstrap = new ProjectBootstrapService(Layout, Store, Coordinator);
    }

    public string Root { get; }
    public ProjectLayout Layout { get; }
    public SqliteStateStore Store { get; }
    public StateCoordinator Coordinator { get; }
    public ProjectBootstrapService Bootstrap { get; }

    public static async Task<TestProject> CreateAsync(string? projectId = null)
    {
        var root = Path.Combine(Path.GetTempPath(), $"hiker-pro-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var layout = new ProjectLayout(root);
        var store = new SqliteStateStore(layout);
        await store.InitializeAsync().ConfigureAwait(false);
        var project = new TestProject(root, store);
        if (projectId is not null)
        {
            _ = await project.Bootstrap.InitializeAsync(projectId).ConfigureAwait(false);
        }

        return project;
    }

    public async ValueTask DisposeAsync()
    {
        await Store.DisposeAsync().ConfigureAwait(false);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(Root))
        {
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(Root, recursive: true);
        }
    }
}
