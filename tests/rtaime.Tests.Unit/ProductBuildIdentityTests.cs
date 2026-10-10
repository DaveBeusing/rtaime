// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Reflection;
using System.Reflection.Emit;
using rtaime.Core;

namespace rtaime.Tests.Unit;

public sealed class ProductBuildIdentityTests
{
    [Theory]
    [InlineData("0.1.0-preview.42", "PREVIEW")]
    [InlineData("0.1.0-rc.1", "RELEASE_CANDIDATE")]
    [InlineData("0.1.0-pr.123.42.1", "DEV")]
    [InlineData("0.1.0-dev.gabc", "DEV")]
    [InlineData("0.1.0", "STABLE")]
    public void Diagnostics_preserve_source_identity_and_normalize_release_stage(string version, string stage)
    {
        const string sha = "0123456789abcdef0123456789abcdef01234567";
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.Run);
        assembly.SetCustomAttribute(new CustomAttributeBuilder(
            typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!, [version + "+g" + sha]));
        assembly.SetCustomAttribute(new CustomAttributeBuilder(
            typeof(AssemblyMetadataAttribute).GetConstructor([typeof(string), typeof(string)])!, ["RtaimeBuildId", "release-42-1"]));
        var info = ProductBuildInfo.FromAssembly(assembly);
        Assert.Equal(version, info.ProductVersion);
        Assert.Equal(stage, info.ReleaseStage);
        Assert.Equal(sha, info.SourceRevision);
        Assert.Equal("0123456789ab", info.ShortRevision);
        Assert.Equal("release-42-1", info.BuildId);
        var snapshot = new SupportSnapshotBuilder(info, "test", 1, "running", "healthy").Build();
        Assert.Equal(sha, snapshot.Identity["sourceRevision"]);
        Assert.Equal(info.BuildId, snapshot.Identity["buildId"]);
    }
}
