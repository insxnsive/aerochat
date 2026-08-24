using System.IO;

namespace Aerochat.VisualShell.Tests;

internal static class RepositoryRoot
{
    public static string Path { get; } = Find();

    private static string Find()
    {
        for (DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
             directory is not null; directory = directory.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "Aerochat.sln")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException("Could not find Aerochat.sln above the test output directory.");
    }
}
