using System.Xml.Linq;

namespace rtaime.Tests.Architecture;

public sealed class CoreArchitectureTests
{
    [Fact]
    public void Core_has_no_project_references()
    {
        var document = XDocument.Load(Path.Combine(FindRepositoryRoot(), "src", "rtaime.Core", "rtaime.Core.csproj"));

        Assert.Empty(document.Descendants("ProjectReference"));
    }

    [Fact]
    public void Core_remains_package_and_direct_assembly_reference_neutral()
    {
        var document = XDocument.Load(Path.Combine(FindRepositoryRoot(), "src", "rtaime.Core", "rtaime.Core.csproj"));

        Assert.Empty(document.Descendants("PackageReference"));
        Assert.Empty(document.Descendants("Reference"));
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
