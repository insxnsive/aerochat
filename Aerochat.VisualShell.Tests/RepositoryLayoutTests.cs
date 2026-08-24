using System.IO;
using System.Text.RegularExpressions;

namespace Aerochat.VisualShell.Tests;

public sealed class RepositoryLayoutTests
{
    private static string Root => RepositoryRoot.Path;

    [Test]
    public void Solution_contains_only_visual_app_and_tests()
    {
        string solution = File.ReadAllText(Path.Combine(Root, "Aerochat.sln"));
        string[] names = Regex.Matches(solution, "Project\\(.*?\\) = \\\"([^\\\"]+)\\\"")
            .Select(match => match.Groups[1].Value)
            .Where(name => name != "Solution Items")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.That(names, Is.EqualTo(new[] { "Aerochat", "Aerochat.VisualShell.Tests" }));
    }
}
