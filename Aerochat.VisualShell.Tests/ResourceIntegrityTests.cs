using System.IO;
using System.Text.RegularExpressions;
using Aerochat.Presentation;

namespace Aerochat.VisualShell.Tests;

public sealed class ResourceIntegrityTests
{
    [Test]
    public void Retained_packaged_resources_exist()
    {
        string root = RepositoryRoot.Path;
        string product = Path.Combine(root, "Aerochat");
        var references = Directory.EnumerateFiles(product, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => Regex.Matches(File.ReadAllText(path),
                "(?:/Aerochat;component/|pack://application:,,,/Aerochat;component/)([^\\s}\\\"']+)")
                .Select(match => new { path, resource = match.Groups[1].Value.Split('?', '#')[0] }))
            .Where(item => !item.resource.Contains('{'))
            .Where(item => !File.Exists(Path.Combine(product,
                Uri.UnescapeDataString(item.resource).Replace('/', Path.DirectorySeparatorChar))))
            .Select(item => $"{Path.GetRelativePath(root, item.path)} -> {item.resource}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        PresentationState state = DemoData.Create();
        var demoReferences = state.Scenes.Select(scene => scene.File)
            .Concat(state.PreviewImages.Select(image => image.SourceUri))
            .Concat(state.Ads.Select(ad => ad.ImageUri))
            .Select(uri => uri[(uri.IndexOf("component/", StringComparison.Ordinal) + "component/".Length)..])
            .Where(resource => !File.Exists(Path.Combine(product,
                resource.Replace('/', Path.DirectorySeparatorChar))))
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(references, Is.Empty, string.Join(Environment.NewLine, references));
            Assert.That(demoReferences, Is.Empty, string.Join(Environment.NewLine, demoReferences));
        });
    }
}
