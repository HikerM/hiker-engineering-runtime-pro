namespace Hiker.Pro.Activation;

public static class RuntimeUseLock
{
    public static FileStream AcquireExecution(string hikerHome) => Acquire(hikerHome, activation: false);

    public static FileStream AcquireActivation(string hikerHome) => Acquire(hikerHome, activation: true);

    private static FileStream Acquire(string hikerHome, bool activation)
    {
        var state = Path.Combine(Path.GetFullPath(hikerHome), "state");
        Directory.CreateDirectory(state);
        var path = Path.Combine(state, "runtime-use.lock");
        try
        {
            return new FileStream(
                path,
                FileMode.OpenOrCreate,
                activation ? FileAccess.ReadWrite : FileAccess.Read,
                activation ? FileShare.None : FileShare.Read,
                1,
                FileOptions.None);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                activation ? "Runtime activation must wait for the current invocation to finish" : "Runtime activation is in progress",
                exception);
        }
    }
}
