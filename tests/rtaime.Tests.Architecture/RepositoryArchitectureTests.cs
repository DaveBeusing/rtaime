using System.Xml.Linq;

namespace rtaime.Tests.Architecture;

public sealed class RepositoryArchitectureTests
{
    [Fact]
    public void Managed_project_set_is_exactly_the_approved_bootstrap_set()
    {
        var repo = RepositorySnapshot.Load();

        Assert.Equal(28, repo.Projects.Count);
        Assert.Equal(
            ArchitectureSpec.AllProjects.OrderBy(x => x, StringComparer.Ordinal),
            repo.Projects.Keys.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void Solution_contains_exactly_all_managed_projects()
    {
        var repo = RepositorySnapshot.Load();
        var solution = XDocument.Load(Path.Combine(repo.Root, "rtaime.slnx"));
        var entries = solution.Descendants("Project")
            .Select(x => Normalize((string?)x.Attribute("Path") ?? string.Empty))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(28, entries.Length);
        Assert.Equal(
            ArchitectureSpec.AllProjects.OrderBy(x => x, StringComparer.Ordinal),
            entries);
    }

    [Fact]
    public void No_native_visual_cpp_project_exists()
    {
        var repo = RepositorySnapshot.Load();
        var vcxproj = Directory.EnumerateFiles(repo.Root, "*.vcxproj", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path));

        Assert.Empty(vcxproj);
    }

    [Fact]
    public void Production_project_reference_graph_matches_the_approved_graph()
    {
        var repo = RepositorySnapshot.Load();
        var failures = new List<string>();

        foreach (var (source, expected) in ArchitectureSpec.ProductionGraph)
        {
            var actual = repo.Projects[source].ProjectReferences.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var orderedExpected = expected.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (!actual.SequenceEqual(orderedExpected, StringComparer.Ordinal))
            {
                failures.Add($"{source}: expected [{string.Join(", ", orderedExpected)}], actual [{string.Join(", ", actual)}]");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void Every_project_reference_respects_layer_and_host_boundaries()
    {
        var repo = RepositorySnapshot.Load();
        var failures = new List<string>();

        foreach (var source in repo.Projects.Values)
        {
            foreach (var targetPath in source.ProjectReferences)
            {
                if (!repo.Projects.TryGetValue(targetPath, out var target))
                {
                    failures.Add($"{source.RelativePath} -> {targetPath}: unclassified target (fail closed).");
                    continue;
                }

                failures.AddRange(ArchitecturePolicy.Validate(source, target));
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void Core_and_production_contracts_are_package_and_direct_reference_neutral()
    {
        var repo = RepositorySnapshot.Load();
        var failures = repo.Projects.Values
            .Where(p => p.Name == "rtaime.Core" || p.IsProductionContract)
            .SelectMany(p => p.PackageReferences
                .Select(package => $"{p.Name} -> {package}: package reference forbidden.")
                .Concat(p.AssemblyReferences.Select(reference => $"{p.Name} -> {reference}: direct assembly reference forbidden.")))
            .ToArray();

        Assert.Empty(failures);
    }

    [Fact]
    public void Vendor_packages_stay_at_outer_boundaries()
    {
        var repo = RepositorySnapshot.Load();
        var failures = new List<string>();

        foreach (var project in repo.Projects.Values.Where(p => !p.IsTestArtifact))
        {
            foreach (var package in project.PackageReferences)
            {
                var value = package.ToUpperInvariant();

                if (value.Contains("SQLITE", StringComparison.Ordinal) && project.Name != "rtaime.Persistence")
                    failures.Add($"{project.Name} -> {package}: SQLite belongs in rtaime.Persistence.");

                if ((value.Contains("TENSORRT", StringComparison.Ordinal) ||
                     value.Contains("ONNXRUNTIME", StringComparison.Ordinal) ||
                     value.Contains("DIRECTML", StringComparison.Ordinal)) &&
                    project.Name != "rtaime.Provider.Inference")
                {
                    failures.Add($"{project.Name} -> {package}: inference runtime belongs in rtaime.Provider.Inference.");
                }

                if ((value.Contains("CUDA", StringComparison.Ordinal) || value.Contains("NVIDIA", StringComparison.Ordinal)) &&
                    project.Name is not "rtaime.Provider.Gpu" and not "rtaime.Provider.Inference")
                {
                    failures.Add($"{project.Name} -> {package}: GPU/vendor package outside provider boundary.");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void Core_and_production_contracts_do_not_reference_windows_desktop()
    {
        var repo = RepositorySnapshot.Load();
        var failures = repo.Projects.Values
            .Where(p => p.Name == "rtaime.Core" || p.IsProductionContract)
            .Where(p => p.UsesWpf || p.TargetFramework.Contains("-windows", StringComparison.OrdinalIgnoreCase))
            .Select(p => $"{p.Name}: Core/contracts must remain WindowsDesktop/WPF-neutral.")
            .ToArray();

        Assert.Empty(failures);
    }

    [Fact]
    public void Media_and_AI_contract_sources_do_not_leak_known_vendor_types()
    {
        var repo = RepositorySnapshot.Load();
        var failures = new List<string>();
        var rules = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["rtaime.Media.Contracts"] = new[] { "TensorRT", "OnnxRuntime", "DirectML", "CUDA", "NVIDIA", "DeckLink", "Blackmagic", "AJA." },
            ["rtaime.AI.Contracts"] = new[] { "TensorRT", "Microsoft.ML.OnnxRuntime", "OnnxRuntime", "DirectML" }
        };

        foreach (var (projectName, tokens) in rules)
        {
            var project = repo.Projects.Values.Single(p => p.Name == projectName);
            foreach (var file in Directory.EnumerateFiles(project.Directory, "*.cs", SearchOption.AllDirectories).Where(path => !IsBuildOutput(path)))
            {
                var source = File.ReadAllText(file);
                foreach (var token in tokens.Where(token => source.Contains(token, StringComparison.OrdinalIgnoreCase)))
                    failures.Add($"{projectName} -> {token}: vendor/runtime type leaked into contract source.");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Theory]
    [InlineData("rtaime.Runtime", "rtaime.Tests.Unit", false, true, false, "production-to-test")]
    [InlineData("rtaime.Media.Contracts", "rtaime.Provider.Gpu", false, false, true, "contract-to-implementation")]
    [InlineData("rtaime.Operator", "rtaime.Runtime", false, false, false, "operator-bypass")]
    [InlineData("rtaime.ControlHost", "rtaime.RuntimeHost", false, false, false, "host-to-host")]
    public void Negative_architecture_fixtures_are_rejected(
        string source,
        string target,
        bool sourceIsTest,
        bool targetIsTest,
        bool sourceIsProductionContract,
        string expectedRule)
    {
        var failures = ArchitecturePolicy.Validate(
            ProjectInfo.Synthetic(source, sourceIsTest, sourceIsProductionContract),
            ProjectInfo.Synthetic(target, targetIsTest));

        Assert.Contains(failures, failure => failure.Contains(expectedRule, StringComparison.Ordinal));
    }

    private static bool IsBuildOutput(string path)
    {
        var parts = path.Replace('\\', '/').Split('/');
        return parts.Any(part => part is "bin" or "obj");
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('.', '/');
}

internal static class ArchitecturePolicy
{
    private static readonly HashSet<string> Hosts = new(StringComparer.Ordinal)
    {
        "rtaime.ControlHost", "rtaime.RuntimeHost", "rtaime.AIHost", "rtaime.Operator"
    };

    private static readonly HashSet<string> Providers = new(StringComparer.Ordinal)
    {
        "rtaime.Provider.VirtualMedia", "rtaime.Provider.Gpu", "rtaime.Provider.Inference"
    };

    public static IReadOnlyList<string> Validate(ProjectInfo source, ProjectInfo target)
    {
        var failures = new List<string>();

        if (!source.IsTestArtifact && target.IsTestArtifact)
            failures.Add($"{source.Name} -> {target.Name}: production-to-test reference forbidden.");

        if (source.Name == "rtaime.Core" && target.Name.StartsWith("rtaime.", StringComparison.Ordinal))
            failures.Add($"{source.Name} -> {target.Name}: core must have no rtaime dependency.");

        if (source.IsProductionContract && IsImplementation(target.Name))
            failures.Add($"{source.Name} -> {target.Name}: contract-to-implementation reference forbidden.");

        if (source.Name == "rtaime.Control" &&
            (target.Name is "rtaime.Runtime" or "rtaime.Media" or "rtaime.AI" or "rtaime.Persistence" or "rtaime.Operator" || Providers.Contains(target.Name)))
        {
            failures.Add($"{source.Name} -> {target.Name}: control-to-implementation reference forbidden.");
        }

        if (source.Name == "rtaime.Runtime" &&
            (target.Name is "rtaime.Control" or "rtaime.Persistence" or "rtaime.Operator" || Providers.Contains(target.Name)))
        {
            failures.Add($"{source.Name} -> {target.Name}: runtime-to-authority-or-implementation reference forbidden.");
        }

        if (source.Name == "rtaime.Operator" && target.Name != "rtaime.Client")
            failures.Add($"{source.Name} -> {target.Name}: operator-bypass reference forbidden.");

        if (Hosts.Contains(source.Name) && Hosts.Contains(target.Name))
            failures.Add($"{source.Name} -> {target.Name}: host-to-host reference forbidden.");

        return failures;
    }

    private static bool IsImplementation(string name) =>
        name is "rtaime.Control" or "rtaime.Runtime" or "rtaime.Media" or "rtaime.AI" or "rtaime.Persistence" or "rtaime.Recording" or "rtaime.Client" ||
        Providers.Contains(name) || Hosts.Contains(name);
}

internal sealed record ProjectInfo(
    string Name,
    string RelativePath,
    string Directory,
    bool IsTestArtifact,
    bool IsProductionContract,
    string TargetFramework,
    bool UsesWpf,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<string> PackageReferences,
    IReadOnlyList<string> AssemblyReferences)
{
    public static ProjectInfo Load(string root, string path)
    {
        var document = XDocument.Load(path);
        var relativePath = Normalize(Path.GetRelativePath(root, path));
        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var isTest = relativePath.StartsWith("tests/", StringComparison.Ordinal);
        var isProductionContract = relativePath.StartsWith("src/Contracts/", StringComparison.Ordinal);

        var projectReferences = document.Descendants("ProjectReference")
            .Select(x => (string?)x.Attribute("Include"))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => Normalize(Path.GetRelativePath(root, Path.GetFullPath(Path.Combine(directory, x!)))))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        var packageReferences = document.Descendants("PackageReference")
            .Select(x => (string?)x.Attribute("Include"))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .ToArray();

        var assemblyReferences = document.Descendants("Reference")
            .Select(x => (string?)x.Attribute("Include"))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .ToArray();

        var targetFramework = document.Descendants("TargetFramework")
            .Select(x => x.Value.Trim())
            .FirstOrDefault() ?? string.Empty;

        var usesWpf = document.Descendants("UseWPF")
            .Any(x => string.Equals(x.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase));

        return new ProjectInfo(
            name,
            relativePath,
            directory,
            isTest,
            isProductionContract,
            targetFramework,
            usesWpf,
            projectReferences,
            packageReferences,
            assemblyReferences);
    }

    public static ProjectInfo Synthetic(string name, bool isTest, bool isProductionContract = false) =>
        new(
            name,
            string.Empty,
            string.Empty,
            isTest,
            isProductionContract,
            string.Empty,
            false,
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>());

    private static string Normalize(string path) => path.Replace('\\', '/');
}

internal sealed class RepositorySnapshot
{
    private RepositorySnapshot(string root, IReadOnlyDictionary<string, ProjectInfo> projects)
    {
        Root = root;
        Projects = projects;
    }

    public string Root { get; }
    public IReadOnlyDictionary<string, ProjectInfo> Projects { get; }

    public static RepositorySnapshot Load()
    {
        var root = FindRoot();
        var projects = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .Select(path => ProjectInfo.Load(root, path))
            .ToDictionary(project => project.RelativePath, StringComparer.Ordinal);

        var unknown = projects.Keys.Except(ArchitectureSpec.AllProjects, StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0)
            throw new Xunit.Sdk.XunitException($"Unclassified managed project(s) detected (fail closed): {string.Join(", ", unknown)}");

        return new RepositorySnapshot(root, projects);
    }

    private static string FindRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "rtaime.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new Xunit.Sdk.XunitException("Repository root containing rtaime.slnx could not be located.");
    }

    private static bool IsBuildOutput(string path)
    {
        var parts = path.Replace('\\', '/').Split('/');
        return parts.Any(part => part is "bin" or "obj");
    }
}

internal static class ArchitectureSpec
{
    public static readonly HashSet<string> AllProjects = new(StringComparer.Ordinal)
    {
        "src/rtaime.Core/rtaime.Core.csproj",
        "src/Contracts/rtaime.Control.Contracts/rtaime.Control.Contracts.csproj",
        "src/Contracts/rtaime.Runtime.Contracts/rtaime.Runtime.Contracts.csproj",
        "src/Contracts/rtaime.Media.Contracts/rtaime.Media.Contracts.csproj",
        "src/Contracts/rtaime.AI.Contracts/rtaime.AI.Contracts.csproj",
        "src/Contracts/rtaime.Provider.Contracts/rtaime.Provider.Contracts.csproj",
        "src/Control/rtaime.Control/rtaime.Control.csproj",
        "src/Runtime/rtaime.Runtime/rtaime.Runtime.csproj",
        "src/Media/rtaime.Media/rtaime.Media.csproj",
        "src/AI/rtaime.AI/rtaime.AI.csproj",
        "src/Persistence/rtaime.Persistence/rtaime.Persistence.csproj",
        "src/Recording/rtaime.Recording/rtaime.Recording.csproj",
        "src/Providers/rtaime.Provider.VirtualMedia/rtaime.Provider.VirtualMedia.csproj",
        "src/Providers/rtaime.Provider.Gpu/rtaime.Provider.Gpu.csproj",
        "src/Providers/rtaime.Provider.Inference/rtaime.Provider.Inference.csproj",
        "src/Client/rtaime.Client/rtaime.Client.csproj",
        "src/Hosts/rtaime.ControlHost/rtaime.ControlHost.csproj",
        "src/Hosts/rtaime.RuntimeHost/rtaime.RuntimeHost.csproj",
        "src/Hosts/rtaime.AIHost/rtaime.AIHost.csproj",
        "src/Hosts/rtaime.Operator/rtaime.Operator.csproj",
        "tests/rtaime.TestInfrastructure/rtaime.TestInfrastructure.csproj",
        "tests/rtaime.Tests.Unit/rtaime.Tests.Unit.csproj",
        "tests/rtaime.Tests.Contracts/rtaime.Tests.Contracts.csproj",
        "tests/rtaime.Tests.Architecture/rtaime.Tests.Architecture.csproj",
        "tests/rtaime.Tests.Integration/rtaime.Tests.Integration.csproj",
        "tests/rtaime.Tests.Behavioral/rtaime.Tests.Behavioral.csproj",
        "tests/rtaime.Tests.Failure/rtaime.Tests.Failure.csproj",
        "tests/rtaime.Tests.Performance/rtaime.Tests.Performance.csproj"
    };

    public static readonly IReadOnlyDictionary<string, string[]> ProductionGraph = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["src/rtaime.Core/rtaime.Core.csproj"] = Array.Empty<string>(),
        ["src/Contracts/rtaime.Control.Contracts/rtaime.Control.Contracts.csproj"] = new[] { "src/rtaime.Core/rtaime.Core.csproj" },
        ["src/Contracts/rtaime.Media.Contracts/rtaime.Media.Contracts.csproj"] = new[] { "src/rtaime.Core/rtaime.Core.csproj" },
        ["src/Contracts/rtaime.Provider.Contracts/rtaime.Provider.Contracts.csproj"] = new[] { "src/rtaime.Core/rtaime.Core.csproj", "src/Contracts/rtaime.Media.Contracts/rtaime.Media.Contracts.csproj" },
        ["src/Contracts/rtaime.AI.Contracts/rtaime.AI.Contracts.csproj"] = new[] { "src/rtaime.Core/rtaime.Core.csproj", "src/Contracts/rtaime.Media.Contracts/rtaime.Media.Contracts.csproj" },
        ["src/Contracts/rtaime.Runtime.Contracts/rtaime.Runtime.Contracts.csproj"] = new[] { "src/rtaime.Core/rtaime.Core.csproj", "src/Contracts/rtaime.Media.Contracts/rtaime.Media.Contracts.csproj", "src/Contracts/rtaime.Provider.Contracts/rtaime.Provider.Contracts.csproj" },
        ["src/Control/rtaime.Control/rtaime.Control.csproj"] = new[] { "src/rtaime.Core/rtaime.Core.csproj", "src/Contracts/rtaime.Control.Contracts/rtaime.Control.Contracts.csproj", "src/Contracts/rtaime.Runtime.Contracts/rtaime.Runtime.Contracts.csproj", "src/Contracts/rtaime.Media.Contracts/rtaime.Media.Contracts.csproj", "src/Contracts/rtaime.AI.Contracts/rtaime.AI.Contracts.csproj", "src/Contracts/rtaime.Provider.Contracts/rtaime.Provider.Contracts.csproj" },
        ["src/Runtime/rtaime.Runtime/rtaime.Runtime.csproj"] = new[] { "src/rtaime.Core/rtaime.Core.csproj", "src/Contracts/rtaime.Runtime.Contracts/rtaime.Runtime.Contracts.csproj", "src/Contracts/rtaime.Media.Contracts/rtaime.Media.Contracts.csproj", "src/Contracts/rtaime.AI.Contracts/rtaime.AI.Contracts.csproj", "src/Contracts/rtaime.Provider.Contracts/rtaime.Provider.Contracts.csproj" },
        ["src/Media/rtaime.Media/rtaime.Media.csproj"] = new[] { "src/rtaime.Core/rtaime.Core.csproj", "src/Contracts/rtaime.Media.Contracts/rtaime.Media.Contracts.csproj", "src/Contracts/rtaime.Provider.Contracts/rtaime.Provider.Contracts.csproj" },
        ["src/AI/rtaime.AI/rtaime.AI.csproj"] = new[] { "src/rtaime.Core/rtaime.Core.csproj", "src/Contracts/rtaime.AI.Contracts/rtaime.AI.Contracts.csproj", "src/Contracts/rtaime.Media.Contracts/rtaime.Media.Contracts.csproj", "src/Contracts/rtaime.Provider.Contracts/rtaime.Provider.Contracts.csproj" },
        ["src/Persistence/rtaime.Persistence/rtaime.Persistence.csproj"] = new[] { "src/rtaime.Core/rtaime.Core.csproj" },
        ["src/Recording/rtaime.Recording/rtaime.Recording.csproj"] = new[] { "src/rtaime.Core/rtaime.Core.csproj", "src/Contracts/rtaime.Media.Contracts/rtaime.Media.Contracts.csproj", "src/Contracts/rtaime.Provider.Contracts/rtaime.Provider.Contracts.csproj" },
        ["src/Providers/rtaime.Provider.VirtualMedia/rtaime.Provider.VirtualMedia.csproj"] = new[] { "src/rtaime.Core/rtaime.Core.csproj", "src/Contracts/rtaime.Media.Contracts/rtaime.Media.Contracts.csproj", "src/Contracts/rtaime.Provider.Contracts/rtaime.Provider.Contracts.csproj" },
        ["src/Providers/rtaime.Provider.Gpu/rtaime.Provider.Gpu.csproj"] = new[] { "src/rtaime.Core/rtaime.Core.csproj", "src/Contracts/rtaime.Media.Contracts/rtaime.Media.Contracts.csproj", "src/Contracts/rtaime.Provider.Contracts/rtaime.Provider.Contracts.csproj" },
        ["src/Providers/rtaime.Provider.Inference/rtaime.Provider.Inference.csproj"] = new[] { "src/rtaime.Core/rtaime.Core.csproj", "src/Contracts/rtaime.AI.Contracts/rtaime.AI.Contracts.csproj", "src/Contracts/rtaime.Media.Contracts/rtaime.Media.Contracts.csproj", "src/Contracts/rtaime.Provider.Contracts/rtaime.Provider.Contracts.csproj" },
        ["src/Client/rtaime.Client/rtaime.Client.csproj"] = new[] { "src/rtaime.Core/rtaime.Core.csproj", "src/Contracts/rtaime.Control.Contracts/rtaime.Control.Contracts.csproj", "src/Contracts/rtaime.Media.Contracts/rtaime.Media.Contracts.csproj" },
        ["src/Hosts/rtaime.ControlHost/rtaime.ControlHost.csproj"] = new[] { "src/Control/rtaime.Control/rtaime.Control.csproj", "src/Persistence/rtaime.Persistence/rtaime.Persistence.csproj", "src/Contracts/rtaime.Control.Contracts/rtaime.Control.Contracts.csproj", "src/Contracts/rtaime.Runtime.Contracts/rtaime.Runtime.Contracts.csproj" },
        ["src/Hosts/rtaime.RuntimeHost/rtaime.RuntimeHost.csproj"] = new[] { "src/Runtime/rtaime.Runtime/rtaime.Runtime.csproj", "src/Media/rtaime.Media/rtaime.Media.csproj", "src/Recording/rtaime.Recording/rtaime.Recording.csproj", "src/Providers/rtaime.Provider.VirtualMedia/rtaime.Provider.VirtualMedia.csproj", "src/Providers/rtaime.Provider.Gpu/rtaime.Provider.Gpu.csproj", "src/Contracts/rtaime.Runtime.Contracts/rtaime.Runtime.Contracts.csproj" },
        ["src/Hosts/rtaime.AIHost/rtaime.AIHost.csproj"] = new[] { "src/AI/rtaime.AI/rtaime.AI.csproj", "src/Providers/rtaime.Provider.Inference/rtaime.Provider.Inference.csproj", "src/Contracts/rtaime.AI.Contracts/rtaime.AI.Contracts.csproj" },
        ["src/Hosts/rtaime.Operator/rtaime.Operator.csproj"] = new[] { "src/Client/rtaime.Client/rtaime.Client.csproj" }
    };
}