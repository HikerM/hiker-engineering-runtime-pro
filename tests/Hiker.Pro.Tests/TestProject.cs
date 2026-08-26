using Hiker.Pro.State;

namespace Hiker.Pro.Tests;

internal sealed class TestProject : IAsyncDisposable
{
    private TestProject(string root, SqliteStateStore store)
    {
        Root = root;
        Layout = new ProjectLayout(root);
        Store = store;
    }

    public string Root { get; }
    public ProjectLayout Layout { get; }
    public SqliteStateStore Store { get; }

    public static async Task<TestProject> CreateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hiker-pro-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var layout = new ProjectLayout(root);
        var store = new SqliteStateStore(layout);
        await store.InitializeAsync().ConfigureAwait(false);
        return new TestProject(root, store);
    }

    public async ValueTask DisposeAsync()
    {
        await Store.DisposeAsync().ConfigureAwait(false);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
