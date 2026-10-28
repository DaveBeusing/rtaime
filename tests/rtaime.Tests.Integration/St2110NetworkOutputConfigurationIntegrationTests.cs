// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class St2110NetworkOutputConfigurationIntegrationTests
{
    private const string LoopbackConfig = """
        [{
          "roleId": "program",
          "targetId": "st2110-reference",
          "protocol": "st2110",
          "softwareLoopback": true,
          "address": "127.0.0.1",
          "videoPort": 5004,
          "audioPort": 5006,
          "videoSsrc": 2233,
          "audioSsrc": 2234,
          "queueCapacity": 2
        }]
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Opt_in_loopback_transport_admits_exact_software_runtime_tuple(bool fractional)
    {
        var format = fractional
            ? VideoFormat.Hd1080p59_94Rgba8
            : VideoFormat.Hd1080p50Rgba8;
        var output = Assert.Single(Load(LoopbackConfig, format));
        Assert.Equal(NetworkOutputProtocolFamily.St2110, output.Configuration.Protocol);
        Assert.Equal("program", output.RoleId);
        Assert.Equal(format, output.Configuration.VideoFormat);
        Assert.NotNull(output.Configuration.St2110Settings);
        Assert.Equal("rtp://127.0.0.1:5004,5006;software-loopback",
            output.Configuration.SafeTargetIdentity);
        Assert.Equal(NetworkOutputVideoCodec.Rfc4175Ycbcr422TenBit,
            output.Configuration.VideoCodec);
        Assert.Equal(NetworkOutputAudioCodec.Pcm24, output.Configuration.AudioCodec);
        Assert.Equal(2, output.Configuration.QueueCapacity);
    }

    [Theory]
    [InlineData("\"softwareLoopback\": true", "\"softwareLoopback\": false")]
    [InlineData("\"127.0.0.1\"", "\"192.0.2.1\"")]
    [InlineData("\"videoPort\": 5004", "\"videoPort\": 5005")]
    [InlineData("\"audioPort\": 5006", "\"audioPort\": 6000")]
    [InlineData("\"videoSsrc\": 2233", "\"videoSsrc\": 0")]
    public void Unsafe_or_incomplete_endpoint_configuration_fails_closed(
        string original, string replacement)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            Load(LoopbackConfig.Replace(original, replacement),
                VideoFormat.Hd1080p50Rgba8));
    }

    [Fact]
    public void Protocol_settings_are_rejected_when_mixed_or_misrepresented()
    {
        Assert.Throws<ArgumentException>(() =>
            Load(LoopbackConfig.Replace("\"queueCapacity\": 2",
                "\"endpoint\": \"srt://127.0.0.1:9000\", \"queueCapacity\": 2"),
                VideoFormat.Hd1080p50Rgba8));
        var ndi = """
            [{"roleId":"program","targetId":"ndi","protocol":"ndi",
              "sourceName":"rtaime Program", "softwareLoopback":true}]
            """;
        Assert.Throws<ArgumentException>(() => Load(ndi, VideoFormat.Hd1080p50Rgba8));
        var srt = """
            [{"roleId":"program","targetId":"srt","protocol":"srt",
              "endpoint":"srt://127.0.0.1:9000","videoPort":5004}]
            """;
        Assert.Throws<ArgumentException>(() => Load(srt, VideoFormat.Hd1080p50Rgba8));
    }

    [Fact]
    public void Default_network_outputs_remain_unchanged_without_explicit_opt_in()
    {
        Assert.Empty(RuntimeNetworkOutputConfigurationLoader.Load(
            Array.Empty<string>(), _ => null, VideoFormat.Hd1080p50Rgba8));
        var legacy = """
            [{"roleId":"program","targetId":"old-srt","endpoint":"srt://127.0.0.1:9000"}]
            """;
        var target = Assert.Single(Load(legacy, VideoFormat.Hd1080p50Rgba8));
        Assert.Equal(NetworkOutputProtocolFamily.Srt, target.Configuration.Protocol);
        Assert.Null(target.Configuration.St2110Settings);
    }

    private static IReadOnlyList<RuntimeNetworkOutputTarget> Load(
        string json, VideoFormat format) =>
        RuntimeNetworkOutputConfigurationLoader.Load(
            new[] { $"--network-outputs={json}" }, _ => null, format);
}
