using System.IO;
using System.Text.RegularExpressions;

namespace Aerochat.VisualShell.Tests;

public sealed class RepositoryLayoutTests
{
    private static string Root => RepositoryRoot.Path;

    [Test]
    public void Product_source_contains_no_backend_or_external_side_effect_code()
    {
        string[] forbiddenDirectories =
        [
            "DSP", "Aerovoice", "Aerobool", "Aerotest", "Installer", "Dynamic",
            "Aerochat/Voice", "Aerochat/Services", "Aerochat/Hoarder", "Aerochat/Protobuf",
            "Aerochat/Settings", "Aerochat/Theme", "Aerochat/ViewModels", "Aerochat/WebDir",
            "Aerochat/AppHostBin"
        ];
        string[] forbiddenTokens =
        [
            "DSharpPlus", "Aerovoice", "DiscordProtos", "Google.Protobuf", "WebView2",
            "Websocket.Client", "HttpClient", "ProtectedData", "NamedPipe", "Process.Start",
            "ShellExecute", "SettingsManager", "DllImport", "Vanara.PInvoke", "System.Speech",
            "System.Drawing", "File.WriteAll", "File.AppendAll", "Directory.CreateDirectory"
        ];
        string[] existingDirectories = forbiddenDirectories
            .Where(path => Directory.Exists(Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar))))
            .ToArray();
        var offenders = Directory.EnumerateFiles(Path.Combine(Root, "Aerochat"), "*.*", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                        && !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(path => new { path, text = File.ReadAllText(path) })
            .SelectMany(file => forbiddenTokens.Where(file.text.Contains)
                .Select(token => $"{Path.GetRelativePath(Root, file.path)}: {token}"))
            .ToArray();

        string project = File.ReadAllText(Path.Combine(Root, "Aerochat", "Aerochat.csproj"));
        string[] packages = Regex.Matches(project, "PackageReference Include=\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        string[] projectReferences = Regex.Matches(project, "ProjectReference Include=")
            .Select(_ => "ProjectReference")
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(existingDirectories, Is.Empty, string.Join(", ", existingDirectories));
            Assert.That(offenders, Is.Empty, string.Join(Environment.NewLine, offenders));
            Assert.That(projectReferences, Is.Empty);
            Assert.That(packages, Is.EqualTo(new[] { "XamlAnimatedGif" }));
            Assert.That(project, Does.Not.Contain("PreferNativeArm64"));
        });
    }

    [Test]
    public void Solution_contains_only_visual_app_and_tests()
    {
        string solution = File.ReadAllText(Path.Combine(Root, "Aerochat.sln"));
        string[] names = Regex.Matches(solution, "Project\\(.*?\\) = \\\"([^\\\"]+)\\\"")
            .Select(match => match.Groups[1].Value)
            .Where(name => name != "Solution Items")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.That(names, Is.EqualTo(new[] { "Aerochat", "Aerochat.Server", "Aerochat.VisualShell.Tests" }));
    }

    [Test]
    public void Solution_contains_client_and_server_projects()
    {
        string solution = File.ReadAllText(Path.Combine(Root, "Aerochat.sln"));
        string[] projectLines = solution.Split('\n')
            .Where(line => line.StartsWith("Project(", StringComparison.Ordinal))
            .Where(line => !line.Contains("= \"Solution Items\",", StringComparison.Ordinal))
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(projectLines, Has.Some.Contains("\"Aerochat\\Aerochat.csproj\""));
            Assert.That(projectLines, Has.Some.Contains("\"Aerochat.Server\\Aerochat.Server.csproj\""));
            Assert.That(projectLines, Has.Length.EqualTo(3));
            Assert.That(projectLines, Has.None.Contains("\"Aerotest.csproj\""));
        });
    }

    [Test]
    public void Server_project_has_no_wpf_dependencies()
    {
        string project = File.ReadAllText(Path.Combine(Root, "Aerochat.Server", "Aerochat.Server.csproj"));

        Assert.Multiple(() =>
        {
            Assert.That(project, Does.Not.Contain("UseWPF"));
            Assert.That(project, Does.Not.Contain("net8.0-windows"));
            Assert.That(project, Does.Contain("Microsoft.NET.Sdk.Web"));
        });
    }
}
