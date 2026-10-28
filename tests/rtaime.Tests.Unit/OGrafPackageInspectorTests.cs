// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Client;

namespace rtaime.Tests.Unit;

public sealed class OGrafPackageInspectorTests
{
    private const string ExampleManifest = """
        {
          "$schema": "https://ograf.ebu.io/v1/specification/json-schemas/graphics/schema.json",
          "id": "com.rtaime.test.lower-third",
          "name": "Test lower third",
          "version": "1.0",
          "main": "graphic.mjs",
          "supportsRealTime": true,
          "supportsNonRealTime": false,
          "stepCount": 1,
          "schema": {
            "type": "object",
            "properties": {
              "caption": { "type": "string", "default": "Sample" },
              "visible": { "type": "boolean", "default": false }
            },
            "required": [ "caption" ]
          },
          "customActions": [ { "id": "reset", "name": "Reset" } ],
          "actionDurations": [
            { "type": "playAction", "duration": 500, "steps": [ { "step": 0, "duration": 400 } ] },
            { "type": "customAction", "customActionId": "reset", "duration": 0 }
          ],
          "renderRequirements": [
            { "accessToPublicInternet": { "exact": false } }
          ],
          "thumbnails": [ { "file": "thumbnail.png" } ]
        }
        """;

    [Fact]
    public async Task Valid_v1_package_is_inspected_without_granting_render_or_program_rights()
    {
        await using var fixture = await Package.CreateAsync();
        var result = await OGrafPackageInspector.InspectAsync(fixture.Manifest);
        Assert.Equal("com.rtaime.test.lower-third", result.GraphicId);
        Assert.Equal("Test lower third", result.Name);
        Assert.Equal("1.0", result.Version);
        Assert.EndsWith("graphic.mjs", result.MainPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(64, result.MainSha256.Length);
        Assert.Equal(64, result.ManifestSha256.Length);
        Assert.Contains(result.Parameters, p =>
            p.Name == "caption" && p.Type == "string" && p.Required);
        Assert.Contains(result.ActionDurations, d =>
            d.Type == "playAction" && d.DurationMilliseconds == 500);
        Assert.Single(result.CustomActionIds);
        Assert.True(result.SupportsRealTime);
        Assert.False(result.RendererQualified);
        Assert.False(result.PreviewFrameConfirmed);
        Assert.False(result.CanTakeToProgram);
        Assert.Contains("UNAVAILABLE", result.AdmissionStatus, StringComparison.Ordinal);
        Assert.NotNull(result.ThumbnailPath);
        var image = await OGrafPackageInspector.ReadThumbnailAsync(result);
        Assert.NotNull(image);
        Assert.Equal(8, image!.Length);
    }

    [Theory]
    [InlineData("../outside.mjs")]
    [InlineData("folder/../../outside.mjs")]
    [InlineData("https://malicious.example/bundle.mjs")]
    [InlineData("file:///C:/secret.mjs")]
    [InlineData("C:\\temp\\script.mjs")]
    [InlineData("subdir\\script.mjs")]
    [InlineData("//attacker/share.mjs")]
    [InlineData("percent%2Fslash.mjs")]
    public async Task Unsafe_or_nonlocal_main_reference_is_rejected(string main)
    {
        await using var fixture = await Package.CreateAsync();
        await fixture.ReplaceAsync("\"main\": \"graphic.mjs\"",
            "\"main\": \"" + main.Replace("\\", "\\\\", StringComparison.Ordinal) + "\"");
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await OGrafPackageInspector.InspectAsync(fixture.Manifest));
    }

    [Fact]
    public async Task Embedded_infinite_loop_is_never_executed_during_inspection()
    {
        await using var fixture = await Package.CreateAsync();
        await File.WriteAllTextAsync(fixture.MainScript,
            "export default class Graphic extends HTMLElement { load() { while(true) {} } }");
        var inspected = await OGrafPackageInspector.InspectAsync(fixture.Manifest)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(inspected.CanTakeToProgram);
        Assert.Equal(64, inspected.MainSha256.Length);
    }

    [Fact]
    public async Task Public_network_requirement_is_rejected_without_rendering()
    {
        await using var fixture = await Package.CreateAsync();
        await fixture.ReplaceAsync("\"exact\": false", "\"exact\": true");
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await OGrafPackageInspector.InspectAsync(fixture.Manifest));
    }

    [Theory]
    [InlineData("\"supportsRealTime\": true", "\"supportsRealTime\": \"true\"")]
    [InlineData("\"stepCount\": 1", "\"stepCount\": 1024")]
    [InlineData("\"duration\": 500", "\"duration\": 999999")]
    [InlineData("\"type\": \"string\"", "\"type\": \"object\"")]
    [InlineData("\"default\": false", "\"default\": \"false\"")]
    [InlineData("\"id\": \"reset\"", "\"id\": \"playAction\"")]
    public async Task Invalid_manifest_types_and_budgets_are_rejected(string original, string replacement)
    {
        await using var fixture = await Package.CreateAsync();
        await fixture.ReplaceAsync(original, replacement);
        var changed = await File.ReadAllTextAsync(fixture.Manifest);
        Assert.Contains(replacement, changed, StringComparison.Ordinal);
        if (original == "\"id\": \"reset\"")
        {
            // An unmatched custom action duration is invalid.
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await OGrafPackageInspector.InspectAsync(fixture.Manifest));
        }
        else await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await OGrafPackageInspector.InspectAsync(fixture.Manifest));
    }

    [Fact]
    public async Task Duplicate_json_property_keys_are_rejected_not_resolved_by_parser_order()
    {
        await using var fixture = await Package.CreateAsync();
        await fixture.ReplaceAsync("\"name\": \"Test lower third\"",
            "\"name\": \"Test lower third\", \"name\": \"Unreviewed value\"");
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await OGrafPackageInspector.InspectAsync(fixture.Manifest));
    }

    [Fact]
    public async Task Missing_resource_and_excessive_bytes_fail_closed()
    {
        await using var fixture = await Package.CreateAsync();
        File.Delete(fixture.MainScript);
        await Assert.ThrowsAnyAsync<IOException>(async () =>
            await OGrafPackageInspector.InspectAsync(fixture.Manifest));
        await File.WriteAllBytesAsync(fixture.MainScript,
            new byte[OGrafV1Profile.MaxScriptBytes + 1]);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await OGrafPackageInspector.InspectAsync(fixture.Manifest));
    }

    [Fact]
    public async Task Wrong_schema_and_missing_required_parameter_are_rejected()
    {
        await using var fixture = await Package.CreateAsync();
        await fixture.ReplaceAsync("v1/specification/json-schemas/graphics",
            "v2/specification/json-schemas/graphics");
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await OGrafPackageInspector.InspectAsync(fixture.Manifest));

        await fixture.ReplaceAsync("v2/specification/json-schemas/graphics",
            "v1/specification/json-schemas/graphics");
        await fixture.ReplaceAsync("\"required\": [ \"caption\" ]",
            "\"required\": [ \"nonexistent\" ]");
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await OGrafPackageInspector.InspectAsync(fixture.Manifest));
    }

    [Fact]
    public async Task Cancelling_inspection_does_not_create_render_or_program_action()
    {
        await using var fixture = await Package.CreateAsync();
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await OGrafPackageInspector.InspectAsync(fixture.Manifest, stop.Token));
    }

    private sealed class Package : IAsyncDisposable
    {
        private Package(string directory)
        {
            Directory = directory;
            Manifest = Path.Combine(directory, "lower-third.ograf.json");
            MainScript = Path.Combine(directory, "graphic.mjs");
        }

        private string Directory { get; }
        public string Manifest { get; }
        public string MainScript { get; }

        public static async Task<Package> CreateAsync()
        {
            var fixture = new Package(Path.Combine(Path.GetTempPath(), "rtaime-ograf-test-" + Guid.NewGuid().ToString("N")));
            System.IO.Directory.CreateDirectory(fixture.Directory);
            await File.WriteAllTextAsync(fixture.Manifest, ExampleManifest);
            await File.WriteAllTextAsync(fixture.MainScript,
                "export default class Graphic extends HTMLElement {}");
            await File.WriteAllBytesAsync(Path.Combine(fixture.Directory, "thumbnail.png"),
                [137, 80, 78, 71, 13, 10, 26, 10]);
            return fixture;
        }

        public async Task ReplaceAsync(string before, string after)
        {
            var original = await File.ReadAllTextAsync(Manifest);
            Assert.Contains(before, original, StringComparison.Ordinal);
            await File.WriteAllTextAsync(Manifest, original.Replace(before, after, StringComparison.Ordinal));
        }

        public ValueTask DisposeAsync()
        {
            System.IO.Directory.Delete(Directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
