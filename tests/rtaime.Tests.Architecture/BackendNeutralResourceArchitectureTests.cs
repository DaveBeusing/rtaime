namespace rtaime.Tests.Architecture;

public sealed class BackendNeutralResourceArchitectureTests
{
    private static readonly string[] BackendOrPresentationTokens =
    {
        "Cuda",
        "D3D11",
        "Direct3D",
        "Vortice",
        "System.Windows",
        "WindowsGraphicsSharedHandle"
    };

    [Fact]
    public void Control_runtime_and_non_media_contracts_do_not_reference_backend_or_presentation_details()
    {
        var repo = RepositorySnapshot.Load();
        var protectedProjects = new[]
        {
            "rtaime.Control",
            "rtaime.Runtime",
            "rtaime.Control.Contracts",
            "rtaime.Runtime.Contracts",
            "rtaime.Provider.Contracts",
            "rtaime.AI.Contracts"
        };

        var failures = new List<string>();
        foreach (var projectName in protectedProjects)
        {
            var project = repo.Projects.Values.Single(project => project.Name == projectName);
            foreach (var file in SourceFiles(project.Directory))
            {
                var source = File.ReadAllText(file);
                foreach (var token in BackendOrPresentationTokens)
                {
                    if (source.Contains(token, StringComparison.OrdinalIgnoreCase))
                    {
                        failures.Add(
                            $"{projectName}/{Path.GetFileName(file)} -> {token}: backend or presentation detail crossed the stable architecture boundary.");
                    }
                }
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void Media_contracts_keep_windows_shared_handle_metadata_narrow_and_vendor_neutral()
    {
        var repo = RepositorySnapshot.Load();
        var project = repo.Projects.Values.Single(item => item.Name == "rtaime.Media.Contracts");
        var failures = new List<string>();

        foreach (var file in SourceFiles(project.Directory))
        {
            var source = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            foreach (var token in new[] { "Cuda", "D3D11", "Direct3D", "Vortice", "System.Windows" })
            {
                if (source.Contains(token, StringComparison.OrdinalIgnoreCase))
                    failures.Add($"{fileName} -> {token}: media contracts must not expose backend implementation types.");
            }

            if (!string.Equals(fileName, "MonitoringContracts.cs", StringComparison.Ordinal) &&
                source.Contains("WindowsGraphicsSharedHandle", StringComparison.Ordinal))
            {
                failures.Add(
                    $"{fileName}: Windows shared-handle metadata is allowed only in the narrow monitoring transport contract.");
            }
        }

        Assert.Empty(failures);
    }

    private static IEnumerable<string> SourceFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
            {
                var parts = path.Replace('\\', '/').Split('/');
                return !parts.Any(part => part is "bin" or "obj");
            });
}
