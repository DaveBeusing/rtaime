// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Xml.Linq;

namespace rtaime.Tests.Architecture;

public sealed class PlatformNeutralityTests
{
	private static readonly string[] WindowsDesktopForbiddenProjects =
	{
		"src/rtaime.Core/rtaime.Core.csproj",
		"src/Contracts/rtaime.Control.Contracts/rtaime.Control.Contracts.csproj",
		"src/Contracts/rtaime.Runtime.Contracts/rtaime.Runtime.Contracts.csproj",
		"src/Contracts/rtaime.Media.Contracts/rtaime.Media.Contracts.csproj",
		"src/Contracts/rtaime.AI.Contracts/rtaime.AI.Contracts.csproj",
		"src/Contracts/rtaime.Provider.Contracts/rtaime.Provider.Contracts.csproj",
		"src/Runtime/rtaime.Runtime/rtaime.Runtime.csproj"
	};

	private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> ApprovedProductionPackages =
		new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
		{
			["rtaime.Persistence"] = new HashSet<string>(StringComparer.Ordinal)
			{
				"Microsoft.Data.Sqlite"
			},
			["rtaime.AppHost"] = new HashSet<string>(StringComparer.Ordinal)
			{
				"Microsoft.Extensions.Hosting.WindowsServices"
			}
		};

	[Fact]
	public void Core_contracts_and_runtime_do_not_opt_into_WPF_or_WindowsDesktop()
	{
		var root = FindRepositoryRoot();
		var failures = new List<string>();

		foreach (var relativePath in WindowsDesktopForbiddenProjects)
		{
			var project = XDocument.Load(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
			var projectName = Path.GetFileNameWithoutExtension(relativePath);

			var useWpf = project.Descendants("UseWPF").Any(x => string.Equals(x.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase));
			var useWindowsForms = project.Descendants("UseWindowsForms").Any(x => string.Equals(x.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase));
			var windowsDesktopReference = project.Descendants("FrameworkReference")
				.Select(x => (string?)x.Attribute("Include"))
				.Any(x => x?.Contains("WindowsDesktop", StringComparison.OrdinalIgnoreCase) == true);
			var windowsTarget = project.Descendants("TargetFramework")
				.Any(x => x.Value.Contains("-windows", StringComparison.OrdinalIgnoreCase));

			if (useWpf || useWindowsForms || windowsDesktopReference || windowsTarget)
				failures.Add($"{projectName}: WPF/WindowsDesktop opt-in is forbidden by the architecture rule.");
		}

		Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
	}

	[Fact]
	public void Production_package_references_stay_at_approved_outer_boundaries()
	{
		var root = FindRepositoryRoot();
		var failures = new List<string>();

		foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories))
		{
			var project = XDocument.Load(path);
			var projectName = Path.GetFileNameWithoutExtension(path);
			var packageReferences = project.Descendants("PackageReference")
				.Select(reference => (string?)reference.Attribute("Include"))
				.Where(package => !string.IsNullOrWhiteSpace(package))
				.Select(package => package!)
				.ToArray();

			foreach (var package in packageReferences)
			{
				if (!ApprovedProductionPackages.TryGetValue(projectName, out var approved) || !approved.Contains(package))
					failures.Add($"{projectName} -> {package}: production package reference is not approved at this architecture boundary.");
			}
		}

		Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
	}

	private static string FindRepositoryRoot()
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
}
