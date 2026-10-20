// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Text.RegularExpressions;

namespace rtaime.Tests.Architecture;

public sealed class ProductScopeCompatibilityTests
{
    [Fact]
    public void Active_program_execution_has_no_historical_product_phase_identity()
    {
        var root = RepositorySnapshot.Load().Root;
        var runtimePath = Path.Combine(root, "src", "Hosts", "rtaime.RuntimeHost");
        var servicePath = Path.Combine(runtimePath, "RuntimeProgramExecutionService.cs");
        var mappingPath = Path.Combine(runtimePath, "RuntimeProgramExecutionService.OutputMapping.cs");

        Assert.True(File.Exists(servicePath), "The single Program execution owner must exist.");
        Assert.True(File.Exists(mappingPath), "Output mapping must remain part of the existing Program execution owner.");
        Assert.False(File.Exists(Path.Combine(runtimePath, "V1RuntimeHostService.cs")));
        Assert.False(File.Exists(Path.Combine(runtimePath, "V1RuntimeHostService.OutputMapping.cs")));

        var source = File.ReadAllText(servicePath);
        Assert.Matches(@"\bclass\s+RuntimeProgramExecutionService\b", source);
        Assert.Matches(@"\bclass\s+ProgramBoundaryResult\b", source);
    }

    [Fact]
    public void Retired_internal_service_names_cannot_reenter_active_sources()
    {
        var root = RepositorySnapshot.Load().Root;
        var sourceRoot = Path.Combine(root, "src");
        var retired = new[] { "V1RuntimeHostService", "V1ProgramBoundaryResult" };
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Split('/').Any(segment => segment is "bin" or "obj"))
                continue;

            var source = File.ReadAllText(file);
            foreach (var symbol in retired)
            {
                if (Regex.IsMatch(source, @"\b" + Regex.Escape(symbol) + @"\b",
                        RegexOptions.CultureInvariant))
                    violations.Add(relative + ": retired active type " + symbol);
            }
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Versioned_external_contracts_and_default_transport_remain_unchanged()
    {
        var root = RepositorySnapshot.Load().Root;
        static string Read(string root, string path) =>
            File.ReadAllText(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));

        var proto = Read(root, "schemas/external-control/v1/external_control.proto");
        Assert.Contains("package rtaime.external_control.v1;", proto, StringComparison.Ordinal);
        Assert.Contains("option csharp_namespace = \"Rtaime.ExternalControl.V1\";", proto, StringComparison.Ordinal);
        Assert.Contains("service ExternalControl", proto, StringComparison.Ordinal);

        var showTemplate = Read(root, "schemas/persistence/v1/show-template.schema.json");
        Assert.Contains("rtaime.show-template.v1", showTemplate, StringComparison.Ordinal);

        var host = Read(root, "src/Hosts/rtaime.RuntimeHost/RuntimeHostProcess.cs");
        Assert.Contains("rtaime.v1.runtime.default", host, StringComparison.Ordinal);
        Assert.Contains("rtaime.v1.ai.default", host, StringComparison.Ordinal);
    }

    [Fact]
    public void Current_product_definition_keeps_scope_separate_from_qualification()
    {
        var root = RepositorySnapshot.Load().Root;
        var scope = File.ReadAllText(Path.Combine(root, "docs", "ProductScope.md"));
        var migration = File.ReadAllText(Path.Combine(root, "docs", "LegacyCompatibilityAndCapabilityMigration.md"));

        Assert.Contains("no historical feature ceiling", scope, StringComparison.Ordinal);
        Assert.Contains("Approved product scope is not evidence", scope, StringComparison.Ordinal);
        Assert.Contains("Reserve before execution", scope, StringComparison.Ordinal);
        Assert.Contains("HARDWARE UNVERIFIED", scope, StringComparison.Ordinal);
        Assert.Contains("COMPATIBILITY_ONLY", migration, StringComparison.Ordinal);
        Assert.Contains("CAPABILITY_GATE", migration, StringComparison.Ordinal);
        Assert.Contains("CONTRACT_MIGRATION", migration, StringComparison.Ordinal);
    }
}
