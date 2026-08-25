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
            "Websocket.Client", "HttpClient", "NamedPipe", "Process.Start",
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
            .Where(path => !path.Replace(Path.DirectorySeparatorChar, '/').Contains("Aerochat/Connectivity/", StringComparison.Ordinal)
                        && !path.Replace(Path.DirectorySeparatorChar, '/').EndsWith("Aerochat/App.xaml.cs", StringComparison.Ordinal))
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
            Assert.That(packages, Is.EqualTo(new[] { "System.Security.Cryptography.ProtectedData", "XamlAnimatedGif" }));
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

        Assert.That(names, Is.EqualTo(new[] { "Aerochat", "Aerochat.Server", "Aerochat.Server.Tests", "Aerochat.VisualShell.Tests" }));
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
            Assert.That(projectLines, Has.Length.EqualTo(4));
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

    [Test]
    public void Client_network_and_secret_storage_stay_inside_connectivity()
    {
        string connectivityRoot = Path.Combine(Root, "Aerochat", "Connectivity");
        string compositionRoot = Path.Combine(Root, "Aerochat", "App.xaml.cs");
        string[] forbiddenTokens =
        [
            "System.Net.Http", "System.Net.WebSockets", "TcpListener", "ProtectedData", "Process.Start"
        ];

        var offenders = Directory.EnumerateFiles(Path.Combine(Root, "Aerochat"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                        && !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .Where(path => !path.StartsWith(connectivityRoot, StringComparison.OrdinalIgnoreCase)
                        && !StringComparer.OrdinalIgnoreCase.Equals(path, compositionRoot))
            .Select(path => new { path, text = File.ReadAllText(path) })
            .SelectMany(file => forbiddenTokens.Where(file.text.Contains)
                .Select(token => $"{Path.GetRelativePath(Root, file.path)}: {token}"))
            .ToArray();

        Assert.That(offenders, Is.Empty, string.Join(Environment.NewLine, offenders));
    }
}
