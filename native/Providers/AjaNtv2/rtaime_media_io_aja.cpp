// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

#include "rtaime_media_io_abi.h"

#include "ajantv2/includes/ntv2card.h"
#include "ajantv2/includes/ntv2devicescanner.h"
#include "ajantv2/includes/ntv2devicefeatures.h"
#include "ajantv2/includes/ntv2formatdescriptor.h"
#include "ajantv2/includes/ntv2utils.h"

#include <algorithm>
#include <array>
#include <chrono>
#include <cmath>
#include <cstdlib>
#include <cstring>
#include <limits>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

#ifndef RTAIME_AJA_SDK_REVISION
#define RTAIME_AJA_SDK_REVISION "release-unverified"
#endif

namespace
{
	constexpr uint32_t kAbiMajor = 1;
	constexpr uint32_t kAbiMinor = 1;
	constexpr uint32_t kPortCount = 3;
	constexpr uint32_t kAutoCirculateFrames = 7;
	constexpr uint32_t kStereoChannels = 2;

	struct Provider;

	struct Session
	{
		Provider* provider{};
		rtaime_media_io_session_config config{};
		uint32_t port_index{};
		NTV2Channel channel{NTV2_CHANNEL_INVALID};
		NTV2InputSource input_source{NTV2_INPUTSOURCE_INVALID};
		NTV2OutputDestination output_destination{NTV2_OUTPUTDESTINATION_INVALID};
		NTV2AudioSystem audio_system{NTV2_AUDIOSYSTEM_INVALID};
		NTV2VideoFormat video_format{NTV2_FORMAT_UNKNOWN};
		std::unique_ptr<NTV2FormatDescriptor> format_descriptor;
		NTV2Buffer video_buffer;
		NTV2Buffer audio_buffer;
		std::vector<float> normalized_audio;
		std::vector<int32_t> output_audio;
		rtaime_media_io_identity active_lease{};
		uint64_t sequence{};
		bool input_lease_active{};
		bool running{};
	};

	struct Provider
	{
		CNTV2Card card;
		std::mutex gate;
		std::array<bool, kPortCount> reserved{};
		std::string device_spec;
	};

	static void copy_text(char* destination, size_t capacity, const std::string& value)
	{
		if (destination == nullptr || capacity == 0)
			return;
		const size_t count = std::min(capacity - 1, value.size());
		std::memcpy(destination, value.data(), count);
		destination[count] = '\0';
	}

	static void fill_identity(rtaime_media_io_identity& identity, uint8_t scope, uint8_t ordinal)
	{
		std::memset(identity.bytes, 0, sizeof(identity.bytes));
		identity.bytes[0] = 0x95;
		identity.bytes[1] = scope;
		identity.bytes[15] = ordinal;
	}

	static bool equal_identity(const rtaime_media_io_identity& left, const rtaime_media_io_identity& right)
	{
		return std::memcmp(left.bytes, right.bytes, sizeof(left.bytes)) == 0;
	}

	static rtaime_media_io_identity port_identity(uint32_t index)
	{
		rtaime_media_io_identity identity{};
		fill_identity(identity, 0x01, static_cast<uint8_t>(index + 1));
		return identity;
	}

	static rtaime_media_io_identity resource_identity(uint32_t index)
	{
		rtaime_media_io_identity identity{};
		fill_identity(identity, 0x02, static_cast<uint8_t>(index + 1));
		return identity;
	}

	static rtaime_media_io_identity source_identity(uint32_t index)
	{
		rtaime_media_io_identity identity{};
		fill_identity(identity, 0x03, static_cast<uint8_t>(index + 1));
		return identity;
	}

	static rtaime_media_io_identity lease_identity(uint32_t port_index, uint64_t sequence)
	{
		rtaime_media_io_identity identity{};
		fill_identity(identity, static_cast<uint8_t>(0x10 + port_index), static_cast<uint8_t>((sequence & 0xffu) + 1u));
		for (size_t index = 0; index < sizeof(sequence); ++index)
			identity.bytes[7 + index] ^= static_cast<uint8_t>((sequence >> (index * 8u)) & 0xffu);
		return identity;
	}

	static int64_t utc_unix_milliseconds()
	{
		const auto now = std::chrono::system_clock::now().time_since_epoch();
		return std::chrono::duration_cast<std::chrono::milliseconds>(now).count();
	}

	static bool is_v1_format(const rtaime_media_io_video_format& format)
	{
		if (format.width != 1920u || format.height != 1080u || format.progressive == 0u || format.pixel_format != RTAIME_MEDIA_IO_RGBA8)
			return false;
		return (format.frame_rate_numerator == 50u && format.frame_rate_denominator == 1u) ||
			(format.frame_rate_numerator == 60000u && format.frame_rate_denominator == 1001u);
	}

	static NTV2VideoFormat requested_video_format(const rtaime_media_io_video_format& format)
	{
		if (format.frame_rate_numerator == 50u && format.frame_rate_denominator == 1u)
			return NTV2_FORMAT_1080p_5000_A;
		if (format.frame_rate_numerator == 60000u && format.frame_rate_denominator == 1001u)
			return NTV2_FORMAT_1080p_5994_A;
		return NTV2_FORMAT_UNKNOWN;
	}

	static bool matches_rate(NTV2VideoFormat detected, const rtaime_media_io_video_format& requested)
	{
		if (requested.frame_rate_numerator == 50u && requested.frame_rate_denominator == 1u)
			return detected == NTV2_FORMAT_1080p_5000_A || detected == NTV2_FORMAT_1080p_5000_B;
		if (requested.frame_rate_numerator == 60000u && requested.frame_rate_denominator == 1001u)
			return detected == NTV2_FORMAT_1080p_5994_A || detected == NTV2_FORMAT_1080p_5994_B;
		return false;
	}

	static bool resolve_port(const rtaime_media_io_identity& id, uint32_t& index)
	{
		for (uint32_t candidate = 0; candidate < kPortCount; ++candidate)
		{
			if (equal_identity(id, port_identity(candidate)))
			{
				index = candidate;
				return true;
			}
		}
		return false;
	}

	static NTV2Channel channel_for_port(uint32_t index)
	{
		switch (index)
		{
			case 0: return NTV2_CHANNEL1;
			case 1: return NTV2_CHANNEL2;
			case 2: return NTV2_CHANNEL3;
			default: return NTV2_CHANNEL_INVALID;
		}
	}

	static NTV2InputSource input_source_for_port(uint32_t index)
	{
		switch (index)
		{
			case 0: return NTV2_INPUTSOURCE_SDI1;
			case 1: return NTV2_INPUTSOURCE_SDI2;
			default: return NTV2_INPUTSOURCE_INVALID;
		}
	}

	static void route_input(CNTV2Card& card, NTV2Channel channel, NTV2InputSource source)
	{
		const NTV2OutputCrosspointID input_output = ::GetInputSourceOutputXpt(source);
		const NTV2InputCrosspointID frame_input = ::GetFrameBufferInputXptFromChannel(channel);
		const NTV2InputCrosspointID csc_input = ::GetCSCInputXptFromChannel(channel);
		const NTV2OutputCrosspointID csc_rgb = ::GetCSCOutputXptFromChannel(channel, false, true);
		card.Connect(csc_input, input_output);
		card.Connect(frame_input, csc_rgb);
	}

	static void route_output(CNTV2Card& card, NTV2Channel channel, NTV2OutputDestination destination)
	{
		const NTV2InputCrosspointID output_input = ::GetOutputDestInputXpt(destination);
		const NTV2OutputCrosspointID frame_output = ::GetFrameBufferOutputXptFromChannel(channel, true);
		const NTV2InputCrosspointID csc_input = ::GetCSCInputXptFromChannel(channel);
		const NTV2OutputCrosspointID csc_yuv = ::GetCSCOutputXptFromChannel(channel);
		card.Connect(csc_input, frame_output);
		card.Connect(output_input, csc_yuv);
	}

	static bool configure_audio_input(Session& session)
	{
		if (session.provider->card.features().GetNumAudioSystems() == 0)
			return false;
		session.audio_system = session.provider->card.features().GetNumAudioSystems() > 1
			? ::NTV2ChannelToAudioSystem(session.channel)
			: NTV2_AUDIOSYSTEM_1;
		auto& card = session.provider->card;
		card.SetAudioSystemInputSource(
			session.audio_system,
			::NTV2InputSourceToAudioSource(session.input_source),
			::NTV2InputSourceToEmbeddedAudioInput(session.input_source));
		card.SetNumberAudioChannels(kStereoChannels, session.audio_system);
		card.SetAudioRate(NTV2_AUDIO_48K, session.audio_system);
		card.SetAudioBufferSize(NTV2_AUDIO_BUFFER_BIG, session.audio_system);
		card.SetAudioLoopBack(NTV2_AUDIO_LOOPBACK_OFF, session.audio_system);
		card.StopAudioInput(session.audio_system);
		card.SetAudioCaptureEnable(session.audio_system, true);
		return session.audio_buffer.Allocate(NTV2_AUDIOSIZE_MAX, true);
	}

	static bool configure_audio_output(Session& session)
	{
		if (session.provider->card.features().GetNumAudioSystems() == 0)
			return false;
		session.audio_system = session.provider->card.features().GetNumAudioSystems() > 1
			? ::NTV2ChannelToAudioSystem(session.channel)
			: NTV2_AUDIOSYSTEM_1;
		auto& card = session.provider->card;
		card.SetNumberAudioChannels(kStereoChannels, session.audio_system);
		card.SetAudioRate(NTV2_AUDIO_48K, session.audio_system);
		card.SetAudioBufferSize(NTV2_AUDIO_BUFFER_BIG, session.audio_system);
		card.SetAudioLoopBack(NTV2_AUDIO_LOOPBACK_OFF, session.audio_system);
		card.SetSDIOutputAudioSystem(session.channel, session.audio_system);
		card.StopAudioOutput(session.audio_system);
		return true;
	}

	static rtaime_media_io_result configure_input(Session& session)
	{
		auto& card = session.provider->card;
		session.input_source = input_source_for_port(session.port_index);
		if (session.input_source == NTV2_INPUTSOURCE_INVALID || !card.features().CanDoInputSource(session.input_source))
			return RTAIME_MEDIA_IO_UNAVAILABLE;

		if (card.features().HasBiDirectionalSDI())
			card.SetSDITransmitEnable(session.channel, false);
		card.EnableChannel(session.channel);
		card.SetMode(session.channel, NTV2_MODE_CAPTURE);
		card.EnableInputInterrupt(session.channel);
		card.SubscribeInputVerticalEvent(session.channel);
		card.SetVANCMode(NTV2_VANCMODE_OFF, session.channel);

		const NTV2VideoFormat detected = card.GetInputVideoFormat(session.input_source);
		if (detected == NTV2_FORMAT_UNKNOWN)
			return RTAIME_MEDIA_IO_UNAVAILABLE;
		if (!matches_rate(detected, session.config.normalized_format))
			return RTAIME_MEDIA_IO_FORMAT_UNSUPPORTED;
		if (!card.features().CanDoFrameBufferFormat(NTV2_FBF_RGBA))
			return RTAIME_MEDIA_IO_FORMAT_UNSUPPORTED;

		session.video_format = detected;
		if (!card.SetVideoFormat(detected, false, false, card.features().CanDoMultiFormat() ? session.channel : NTV2_CHANNEL1))
			return RTAIME_MEDIA_IO_DEVICE_ERROR;
		if (!card.SetFrameBufferFormat(session.channel, NTV2_FBF_RGBA))
			return RTAIME_MEDIA_IO_DEVICE_ERROR;
		route_input(card, session.channel, session.input_source);
		session.format_descriptor = std::make_unique<NTV2FormatDescriptor>(detected, NTV2_FBF_RGBA);
		if (!session.video_buffer.Allocate(session.format_descriptor->GetVideoWriteSize(), true))
			return RTAIME_MEDIA_IO_DEVICE_ERROR;

		const bool with_audio = session.config.embedded_audio_enabled != 0u;
		if (with_audio && !configure_audio_input(session))
			return RTAIME_MEDIA_IO_DEVICE_ERROR;

		card.AutoCirculateStop(session.channel);
		const NTV2AudioSystem audio = with_audio ? session.audio_system : NTV2_AUDIOSYSTEM_INVALID;
		if (!card.AutoCirculateInitForInput(session.channel, kAutoCirculateFrames, audio, AUTOCIRCULATE_WITH_RP188, 1, 0, 0))
			return RTAIME_MEDIA_IO_DEVICE_ERROR;
		if (!card.AutoCirculateStart(session.channel))
			return RTAIME_MEDIA_IO_DEVICE_ERROR;

		session.running = true;
		return RTAIME_MEDIA_IO_OK;
	}

	static rtaime_media_io_result configure_output(Session& session)
	{
		auto& card = session.provider->card;
		if (!card.features().CanDoPlayback())
			return RTAIME_MEDIA_IO_UNAVAILABLE;
		if (!card.features().CanDoFrameBufferFormat(NTV2_FBF_RGBA))
			return RTAIME_MEDIA_IO_FORMAT_UNSUPPORTED;

		session.video_format = requested_video_format(session.config.normalized_format);
		if (session.video_format == NTV2_FORMAT_UNKNOWN || !card.features().CanDoVideoFormat(session.video_format))
			return RTAIME_MEDIA_IO_FORMAT_UNSUPPORTED;
		session.output_destination = ::NTV2ChannelToOutputDestination(session.channel);
		if (!NTV2_OUTPUT_DEST_IS_SDI(session.output_destination))
			return RTAIME_MEDIA_IO_UNAVAILABLE;

		if (card.features().HasBiDirectionalSDI())
			card.SetSDITransmitEnable(session.channel, true);
		card.EnableChannel(session.channel);
		card.SetMode(session.channel, NTV2_MODE_DISPLAY);
		card.EnableOutputInterrupt(session.channel);
		card.SubscribeOutputVerticalEvent(session.channel);
		card.SetVANCMode(NTV2_VANCMODE_OFF, session.channel);
		if (!card.SetVideoFormat(session.video_format, false, false, card.features().CanDoMultiFormat() ? session.channel : NTV2_CHANNEL1))
			return RTAIME_MEDIA_IO_DEVICE_ERROR;
		if (!card.SetFrameBufferFormat(session.channel, NTV2_FBF_RGBA))
			return RTAIME_MEDIA_IO_DEVICE_ERROR;
		route_output(card, session.channel, session.output_destination);

		if (session.config.external_reference_required != 0u)
		{
			if (!card.SetReference(NTV2_REFERENCE_EXTERNAL))
				return RTAIME_MEDIA_IO_UNAVAILABLE;
		}

		const bool with_audio = session.config.embedded_audio_enabled != 0u;
		if (with_audio && !configure_audio_output(session))
			return RTAIME_MEDIA_IO_DEVICE_ERROR;

		card.AutoCirculateStop(session.channel);
		const NTV2AudioSystem audio = with_audio ? session.audio_system : NTV2_AUDIOSYSTEM_INVALID;
		if (!card.AutoCirculateInitForOutput(session.channel, kAutoCirculateFrames, audio, AUTOCIRCULATE_WITH_RP188, 1, 0, 0))
			return RTAIME_MEDIA_IO_DEVICE_ERROR;
		if (!card.AutoCirculateStart(session.channel))
			return RTAIME_MEDIA_IO_DEVICE_ERROR;

		session.running = true;
		return RTAIME_MEDIA_IO_OK;
	}

	static void convert_input_audio(Session& session, uint32_t captured_bytes, uint32_t& sample_count)
	{
		sample_count = 0;
		if (captured_bytes < sizeof(int32_t) * kStereoChannels)
		{
			session.normalized_audio.clear();
			return;
		}

		const size_t value_count = captured_bytes / sizeof(int32_t);
		const size_t stereo_values = value_count - (value_count % kStereoChannels);
		session.normalized_audio.resize(stereo_values);
		const auto* input = static_cast<const int32_t*>(session.audio_buffer.GetHostPointer());
		constexpr double scale = 1.0 / 2147483648.0;
		for (size_t index = 0; index < stereo_values; ++index)
			session.normalized_audio[index] = static_cast<float>(static_cast<double>(input[index]) * scale);
		sample_count = static_cast<uint32_t>(stereo_values / kStereoChannels);
	}

	static void convert_output_audio(Session& session, const rtaime_media_io_output_frame& frame)
	{
		session.output_audio.clear();
		if (frame.audio_opaque_handle == 0u || frame.audio_sample_count == 0u || frame.audio_channel_count != kStereoChannels)
			return;

		const size_t value_count = static_cast<size_t>(frame.audio_sample_count) * frame.audio_channel_count;
		const auto* input = reinterpret_cast<const float*>(static_cast<uintptr_t>(frame.audio_opaque_handle));
		session.output_audio.resize(value_count);
		for (size_t index = 0; index < value_count; ++index)
		{
			const double sample = std::clamp(static_cast<double>(input[index]), -1.0, 1.0);
			const double scaled = sample >= 0.0 ? sample * 2147483647.0 : sample * 2147483648.0;
			session.output_audio[index] = static_cast<int32_t>(std::llround(scaled));
		}
	}
}

struct rtaime_media_io_provider : Provider {};
struct rtaime_media_io_session : Session {};

extern "C" rtaime_media_io_result rtaime_media_io_create_provider(
	uint32_t abi_version_major,
	uint32_t abi_version_minor,
	rtaime_media_io_provider** provider)
{
	if (provider == nullptr)
		return RTAIME_MEDIA_IO_INVALID_ARGUMENT;
	*provider = nullptr;
	if (abi_version_major != kAbiMajor || abi_version_minor > kAbiMinor)
		return RTAIME_MEDIA_IO_FORMAT_UNSUPPORTED;

	auto instance = std::make_unique<rtaime_media_io_provider>();
	const char* configured = std::getenv("RTAIME_AJA_DEVICE");
	instance->device_spec = configured != nullptr && *configured != '\0' ? configured : "0";
	if (!CNTV2DeviceScanner::GetFirstDeviceFromArgument(instance->device_spec, instance->card))
		return RTAIME_MEDIA_IO_UNAVAILABLE;
	if (!instance->card.IsDeviceReady())
		return RTAIME_MEDIA_IO_UNAVAILABLE;
	if (!instance->card.features().CanDoCapture() || !instance->card.features().CanDoPlayback())
		return RTAIME_MEDIA_IO_UNAVAILABLE;
	if (instance->card.features().GetNumVideoChannels() < kPortCount)
		return RTAIME_MEDIA_IO_UNAVAILABLE;
	if (!instance->card.features().CanDoFrameBufferFormat(NTV2_FBF_RGBA))
		return RTAIME_MEDIA_IO_FORMAT_UNSUPPORTED;

	if (instance->card.features().CanDoMultiFormat())
		instance->card.SetMultiFormatMode(true);
	*provider = instance.release();
	return RTAIME_MEDIA_IO_OK;
}

extern "C" void rtaime_media_io_destroy_provider(rtaime_media_io_provider* provider)
{
	delete provider;
}

extern "C" rtaime_media_io_result rtaime_media_io_get_provider_info(
	rtaime_media_io_provider* provider,
	rtaime_media_io_provider_info* info)
{
	if (provider == nullptr || info == nullptr)
		return RTAIME_MEDIA_IO_INVALID_ARGUMENT;
	std::lock_guard<std::mutex> lock(provider->gate);
	std::memset(info, 0, sizeof(*info));
	copy_text(info->adapter_name, sizeof(info->adapter_name), provider->card.GetDisplayName());
	copy_text(info->driver_version, sizeof(info->driver_version), provider->card.GetDriverVersionString());
	copy_text(info->sdk_revision, sizeof(info->sdk_revision), RTAIME_AJA_SDK_REVISION);
	return RTAIME_MEDIA_IO_OK;
}

extern "C" rtaime_media_io_result rtaime_media_io_get_port_count(
	rtaime_media_io_provider* provider,
	uint32_t* port_count)
{
	if (provider == nullptr || port_count == nullptr)
		return RTAIME_MEDIA_IO_INVALID_ARGUMENT;
	*port_count = kPortCount;
	return RTAIME_MEDIA_IO_OK;
}

extern "C" rtaime_media_io_result rtaime_media_io_get_port(
	rtaime_media_io_provider* provider,
	uint32_t index,
	rtaime_media_io_port_info* port)
{
	if (provider == nullptr || port == nullptr || index >= kPortCount)
		return RTAIME_MEDIA_IO_INVALID_ARGUMENT;
	std::memset(port, 0, sizeof(*port));
	port->port_id = port_identity(index);
	port->resource_id = resource_identity(index);
	port->direction = index < 2 ? RTAIME_MEDIA_IO_INPUT : RTAIME_MEDIA_IO_OUTPUT;
	port->transport = RTAIME_MEDIA_IO_SDI;
	port->supports_external_reference = index == 2 ? 1u : 0u;
	return RTAIME_MEDIA_IO_OK;
}

extern "C" rtaime_media_io_result rtaime_media_io_open_session(
	rtaime_media_io_provider* provider,
	const rtaime_media_io_session_config* config,
	rtaime_media_io_session** session)
{
	if (provider == nullptr || config == nullptr || session == nullptr)
		return RTAIME_MEDIA_IO_INVALID_ARGUMENT;
	*session = nullptr;
	if (config->abi_version_major != kAbiMajor || config->abi_version_minor > kAbiMinor)
		return RTAIME_MEDIA_IO_FORMAT_UNSUPPORTED;
	if (!is_v1_format(config->normalized_format))
		return RTAIME_MEDIA_IO_FORMAT_UNSUPPORTED;
	if (config->transfer_mode != RTAIME_MEDIA_IO_PINNED_HOST_LEASE)
		return RTAIME_MEDIA_IO_FORMAT_UNSUPPORTED;

	uint32_t index = 0;
	if (!resolve_port(config->port_id, index))
		return RTAIME_MEDIA_IO_INVALID_ARGUMENT;
	const uint32_t expected_direction = index < 2 ? RTAIME_MEDIA_IO_INPUT : RTAIME_MEDIA_IO_OUTPUT;
	if (config->direction != expected_direction)
		return RTAIME_MEDIA_IO_INVALID_ARGUMENT;

	std::lock_guard<std::mutex> lock(provider->gate);
	if (provider->reserved[index])
		return RTAIME_MEDIA_IO_STATE_ERROR;

	auto instance = std::make_unique<rtaime_media_io_session>();
	instance->provider = provider;
	instance->config = *config;
	instance->port_index = index;
	instance->channel = channel_for_port(index);
	provider->reserved[index] = true;

	const rtaime_media_io_result result = config->direction == RTAIME_MEDIA_IO_INPUT
		? configure_input(*instance)
		: configure_output(*instance);
	if (result != RTAIME_MEDIA_IO_OK)
	{
		provider->reserved[index] = false;
		return result;
	}

	*session = instance.release();
	return RTAIME_MEDIA_IO_OK;
}

extern "C" void rtaime_media_io_close_session(rtaime_media_io_session* session)
{
	if (session == nullptr)
		return;
	auto* provider = session->provider;
	if (provider != nullptr)
	{
		std::lock_guard<std::mutex> lock(provider->gate);
		if (session->running)
		{
			provider->card.AutoCirculateStop(session->channel);
			if (session->config.direction == RTAIME_MEDIA_IO_INPUT)
				provider->card.UnsubscribeInputVerticalEvent(session->channel);
			else
				provider->card.UnsubscribeOutputVerticalEvent(session->channel);
			session->running = false;
		}
		if (session->port_index < provider->reserved.size())
			provider->reserved[session->port_index] = false;
	}
	delete session;
}

extern "C" rtaime_media_io_result rtaime_media_io_get_status(
	rtaime_media_io_session* session,
	rtaime_media_io_port_status* status)
{
	if (session == nullptr || status == nullptr || session->provider == nullptr)
		return RTAIME_MEDIA_IO_INVALID_ARGUMENT;
	std::lock_guard<std::mutex> lock(session->provider->gate);
	std::memset(status, 0, sizeof(*status));
	status->port_id = port_identity(session->port_index);
	status->observed_at_unix_milliseconds = utc_unix_milliseconds();
	if (!session->provider->card.IsDeviceReady())
	{
		status->signal_state = RTAIME_MEDIA_IO_SIGNAL_FAULTED;
		status->vendor_status_code = -1;
		return RTAIME_MEDIA_IO_OK;
	}

	if (session->config.direction == RTAIME_MEDIA_IO_INPUT)
	{
		const NTV2VideoFormat detected = session->provider->card.GetInputVideoFormat(session->input_source);
		if (detected == NTV2_FORMAT_UNKNOWN)
			status->signal_state = RTAIME_MEDIA_IO_SIGNAL_LOST;
		else if (matches_rate(detected, session->config.normalized_format))
			status->signal_state = RTAIME_MEDIA_IO_SIGNAL_LOCKED;
		else
			status->signal_state = RTAIME_MEDIA_IO_SIGNAL_UNSTABLE;
	}
	else
	{
		AUTOCIRCULATE_STATUS ac_status;
		status->signal_state = session->provider->card.AutoCirculateGetStatus(session->channel, ac_status) && ac_status.IsRunning()
			? RTAIME_MEDIA_IO_SIGNAL_LOCKED
			: RTAIME_MEDIA_IO_SIGNAL_FAULTED;
		status->vendor_status_code = status->signal_state == RTAIME_MEDIA_IO_SIGNAL_LOCKED ? 0 : -2;
	}
	return RTAIME_MEDIA_IO_OK;
}

extern "C" rtaime_media_io_result rtaime_media_io_input_try_acquire(
	rtaime_media_io_session* session,
	rtaime_media_io_input_frame* frame)
{
	if (session == nullptr || frame == nullptr || session->provider == nullptr || session->config.direction != RTAIME_MEDIA_IO_INPUT)
		return RTAIME_MEDIA_IO_INVALID_ARGUMENT;
	std::lock_guard<std::mutex> lock(session->provider->gate);
	if (session->input_lease_active)
		return RTAIME_MEDIA_IO_WOULD_BLOCK;

	AUTOCIRCULATE_STATUS status;
	if (!session->provider->card.AutoCirculateGetStatus(session->channel, status) || !status.IsRunning())
		return RTAIME_MEDIA_IO_DEVICE_ERROR;
	if (!status.HasAvailableInputFrame())
		return RTAIME_MEDIA_IO_WOULD_BLOCK;

	AUTOCIRCULATE_TRANSFER transfer;
	if (!transfer.SetVideoBuffer(session->video_buffer, session->video_buffer.GetByteCount()))
		return RTAIME_MEDIA_IO_STATE_ERROR;
	if (session->config.embedded_audio_enabled != 0u)
		transfer.SetAudioBuffer(session->audio_buffer, session->audio_buffer.GetByteCount());
	if (!session->provider->card.AutoCirculateTransfer(session->channel, transfer))
		return RTAIME_MEDIA_IO_DEVICE_ERROR;

	uint32_t audio_samples = 0;
	if (session->config.embedded_audio_enabled != 0u)
		convert_input_audio(*session, transfer.GetCapturedAudioByteCount(), audio_samples);

	const uint64_t sequence = session->sequence++;
	session->active_lease = lease_identity(session->port_index, sequence);
	session->input_lease_active = true;
	std::memset(frame, 0, sizeof(*frame));
	frame->port_id = port_identity(session->port_index);
	frame->source_id = source_identity(session->port_index);
	frame->sequence_number = sequence;
	frame->presentation_timestamp = static_cast<int64_t>(sequence);
	frame->timebase_numerator = session->config.normalized_format.frame_rate_denominator;
	frame->timebase_denominator = session->config.normalized_format.frame_rate_numerator;
	frame->normalized_format = session->config.normalized_format;
	frame->video.lease_id = session->active_lease;
	frame->video.opaque_handle = static_cast<uint64_t>(reinterpret_cast<uintptr_t>(session->video_buffer.GetHostPointer()));
	frame->video.byte_length = session->video_buffer.GetByteCount();
	frame->video.row_bytes = session->config.normalized_format.width * 4u;
	frame->video.transfer_mode = RTAIME_MEDIA_IO_PINNED_HOST_LEASE;
	frame->video.storage_domain = 1u;
	frame->video.native_pixel_format = RTAIME_MEDIA_IO_RGBA8;
	frame->audio_opaque_handle = session->normalized_audio.empty()
		? 0u
		: static_cast<uint64_t>(reinterpret_cast<uintptr_t>(session->normalized_audio.data()));
	frame->audio_sample_count = audio_samples;
	frame->audio_channel_count = session->normalized_audio.empty() ? 0u : kStereoChannels;
	return RTAIME_MEDIA_IO_OK;
}

extern "C" rtaime_media_io_result rtaime_media_io_input_release(
	rtaime_media_io_session* session,
	const rtaime_media_io_identity* lease_id)
{
	if (session == nullptr || lease_id == nullptr || session->provider == nullptr || session->config.direction != RTAIME_MEDIA_IO_INPUT)
		return RTAIME_MEDIA_IO_INVALID_ARGUMENT;
	std::lock_guard<std::mutex> lock(session->provider->gate);
	if (!session->input_lease_active || !equal_identity(*lease_id, session->active_lease))
		return RTAIME_MEDIA_IO_STATE_ERROR;
	session->input_lease_active = false;
	std::memset(&session->active_lease, 0, sizeof(session->active_lease));
	return RTAIME_MEDIA_IO_OK;
}

extern "C" rtaime_media_io_result rtaime_media_io_output_try_submit(
	rtaime_media_io_session* session,
	const rtaime_media_io_output_frame* frame)
{
	if (session == nullptr || frame == nullptr || session->provider == nullptr || session->config.direction != RTAIME_MEDIA_IO_OUTPUT)
		return RTAIME_MEDIA_IO_INVALID_ARGUMENT;
	if (!is_v1_format(frame->format) || frame->opaque_surface_handle == 0u)
		return RTAIME_MEDIA_IO_INVALID_ARGUMENT;
	std::lock_guard<std::mutex> lock(session->provider->gate);

	AUTOCIRCULATE_STATUS status;
	if (!session->provider->card.AutoCirculateGetStatus(session->channel, status) || !status.IsRunning())
		return RTAIME_MEDIA_IO_DEVICE_ERROR;
	if (!status.CanAcceptMoreOutputFrames())
		return RTAIME_MEDIA_IO_WOULD_BLOCK;

	AUTOCIRCULATE_TRANSFER transfer;
	const size_t video_bytes = static_cast<size_t>(frame->format.width) * frame->format.height * 4u;
	NTV2Buffer video(
		reinterpret_cast<void*>(static_cast<uintptr_t>(frame->opaque_surface_handle)),
		video_bytes);
	if (!transfer.SetVideoBuffer(video, video.GetByteCount()))
		return RTAIME_MEDIA_IO_STATE_ERROR;

	convert_output_audio(*session, *frame);
	if (!session->output_audio.empty())
	{
		NTV2Buffer audio(session->output_audio.data(), session->output_audio.size() * sizeof(int32_t));
		transfer.SetAudioBuffer(audio, audio.GetByteCount());
	}

	return session->provider->card.AutoCirculateTransfer(session->channel, transfer)
		? RTAIME_MEDIA_IO_OK
		: RTAIME_MEDIA_IO_DEVICE_ERROR;
}
