using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Tests.Contracts;

public sealed class AudioContractCompatibilityTests
{
    private static readonly JsonSerializerOptions TransportJson = CreateOptions();

    [Fact]
    public void Audio_stream_and_buffer_descriptors_round_trip_without_bulk_samples()
    {
        var videoSourceId = new MediaSourceId(Identity.Parse("91000000-0000-0000-0000-000000000001"));
        var streamId = new AudioStreamId(Identity.Parse("91000000-0000-0000-0000-000000000002"));
        var timingDomainId = Identity.Parse("91000000-0000-0000-0000-000000000003");
        var format = AudioFormat.Stereo48kFloat32;
        var stream = new AudioStreamDescriptor(
            MediaContractVersion.Current,
            streamId,
            videoSourceId,
            format,
            timingDomainId);
        var buffer = new AudioBufferDescriptor(
            MediaContractVersion.Current,
            streamId,
            format,
            timingDomainId,
            new AudioBufferTiming(960, 960, 960, new Timebase(1, 48_000)),
            new OpaqueAudioHandle("test.audio", "buffer-1"));

        var streamJson = JsonSerializer.Serialize(stream, TransportJson);
        var bufferJson = JsonSerializer.Serialize(buffer, TransportJson);
        var streamCopy = JsonSerializer.Deserialize<AudioStreamDescriptor>(streamJson, TransportJson);
        var bufferCopy = JsonSerializer.Deserialize<AudioBufferDescriptor>(bufferJson, TransportJson);

        Assert.NotNull(streamCopy);
        Assert.NotNull(bufferCopy);
        Assert.Equal(stream, streamCopy);
        Assert.Equal(buffer, bufferCopy);
        Assert.DoesNotContain("SampleData", bufferJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Samples", bufferJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("byte[]", bufferJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Audio_stream_identity_uses_canonical_scalar_transport_form()
    {
        var streamId = new AudioStreamId(Identity.Parse("91000000-0000-0000-0000-000000000004"));

        var json = JsonSerializer.Serialize(streamId, TransportJson);
        var copy = JsonSerializer.Deserialize<AudioStreamId>(json, TransportJson);

        Assert.Equal("\"91000000-0000-0000-0000-000000000004\"", json);
        Assert.Equal(streamId, copy);
    }

    [Fact]
    public void Audio_contracts_fail_closed_for_invalid_values_and_unknown_version()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AudioFormat(48_000, (AudioChannelLayout)999, AudioSampleFormat.Float32, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AudioFormat(48_000, AudioChannelLayout.Stereo, (AudioSampleFormat)999, 2));
        Assert.Throws<ArgumentException>(() =>
            new AudioFormat(48_000, AudioChannelLayout.Stereo, AudioSampleFormat.Float32, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AudioBufferTiming(0, 0, 0, new Timebase(1, 48_000)));

        var unknown = new CompatibilityVersion(99, 0);
        Assert.Throws<NotSupportedException>(() => new AudioStreamDescriptor(
            unknown,
            AudioStreamId.New(),
            MediaSourceId.New(),
            AudioFormat.Stereo48kFloat32,
            Identity.New()));
    }

    [Fact]
    public void Additive_audio_contracts_remain_in_media_contract_v1_0_family()
    {
        Assert.Equal(new CompatibilityVersion(1, 0), MediaContractVersion.Current);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new AudioStreamIdJsonConverter());
        options.Converters.Add(new AudioFormatJsonConverter());
        options.Converters.Add(new AudioBufferTimingJsonConverter());
        options.Converters.Add(new ContractScalarJsonConverterFactory());
        return options;
    }

    private sealed class AudioStreamIdJsonConverter : JsonConverter<AudioStreamId>
    {
        public override AudioStreamId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString() ?? throw new JsonException("Audio stream identity is required.");
            return new AudioStreamId(Identity.Parse(value));
        }

        public override void Write(Utf8JsonWriter writer, AudioStreamId value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }

    private sealed class AudioFormatJsonConverter : JsonConverter<AudioFormat>
    {
        public override AudioFormat Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;

            try
            {
                return new AudioFormat(
                    root.GetProperty("sampleRate").GetUInt32(),
                    (AudioChannelLayout)root.GetProperty("channelLayout").GetInt32(),
                    (AudioSampleFormat)root.GetProperty("sampleFormat").GetInt32(),
                    root.GetProperty("channelCount").GetUInt32());
            }
            catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or ArgumentException or OverflowException)
            {
                throw new JsonException("Invalid audio format representation.", exception);
            }
        }

        public override void Write(Utf8JsonWriter writer, AudioFormat value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("sampleRate", value.SampleRate);
            writer.WriteNumber("channelLayout", (int)value.ChannelLayout);
            writer.WriteNumber("sampleFormat", (int)value.SampleFormat);
            writer.WriteNumber("channelCount", value.ChannelCount);
            writer.WriteEndObject();
        }
    }

    private sealed class AudioBufferTimingJsonConverter : JsonConverter<AudioBufferTiming>
    {
        public override AudioBufferTiming Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;

            try
            {
                var samplePosition = ulong.Parse(
                    root.GetProperty("samplePosition").GetString() ?? throw new JsonException("Sample position is required."),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture);
                var sampleCount = uint.Parse(
                    root.GetProperty("sampleCount").GetString() ?? throw new JsonException("Sample count is required."),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture);
                var presentationTimestamp = long.Parse(
                    root.GetProperty("presentationTimestamp").GetString() ?? throw new JsonException("Presentation timestamp is required."),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture);
                var timebase = Timebase.Parse(
                    root.GetProperty("timebase").GetString() ?? throw new JsonException("Timebase is required."));

                return new AudioBufferTiming(samplePosition, sampleCount, presentationTimestamp, timebase);
            }
            catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException or OverflowException)
            {
                throw new JsonException("Invalid audio buffer timing representation.", exception);
            }
        }

        public override void Write(Utf8JsonWriter writer, AudioBufferTiming value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("samplePosition", value.SamplePosition.ToString(CultureInfo.InvariantCulture));
            writer.WriteString("sampleCount", value.SampleCount.ToString(CultureInfo.InvariantCulture));
            writer.WriteString("presentationTimestamp", value.PresentationTimestamp.ToString(CultureInfo.InvariantCulture));
            writer.WriteString("timebase", value.Timebase.ToString());
            writer.WriteEndObject();
        }
    }
}
