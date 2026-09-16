// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

#pragma once

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define RTAIME_MEDIA_IO_ABI_VERSION_MAJOR 1u
#define RTAIME_MEDIA_IO_ABI_VERSION_MINOR 1u
#define RTAIME_MEDIA_IO_IDENTITY_BYTES 16u

typedef struct rtaime_media_io_provider rtaime_media_io_provider;
typedef struct rtaime_media_io_session rtaime_media_io_session;

typedef enum rtaime_media_io_result
{
	RTAIME_MEDIA_IO_OK = 0,
	RTAIME_MEDIA_IO_WOULD_BLOCK = 1,
	RTAIME_MEDIA_IO_INVALID_ARGUMENT = 2,
	RTAIME_MEDIA_IO_UNAVAILABLE = 3,
	RTAIME_MEDIA_IO_FORMAT_UNSUPPORTED = 4,
	RTAIME_MEDIA_IO_DEVICE_ERROR = 5,
	RTAIME_MEDIA_IO_STATE_ERROR = 6
} rtaime_media_io_result;

typedef enum rtaime_media_io_direction
{
	RTAIME_MEDIA_IO_INPUT = 1,
	RTAIME_MEDIA_IO_OUTPUT = 2
} rtaime_media_io_direction;

typedef enum rtaime_media_io_transport
{
	RTAIME_MEDIA_IO_SDI = 1
} rtaime_media_io_transport;

typedef enum rtaime_media_io_signal_state
{
	RTAIME_MEDIA_IO_SIGNAL_UNKNOWN = 1,
	RTAIME_MEDIA_IO_SIGNAL_LOCKED = 2,
	RTAIME_MEDIA_IO_SIGNAL_UNSTABLE = 3,
	RTAIME_MEDIA_IO_SIGNAL_LOST = 4,
	RTAIME_MEDIA_IO_SIGNAL_FAULTED = 5
} rtaime_media_io_signal_state;

typedef enum rtaime_media_io_transfer_mode
{
	RTAIME_MEDIA_IO_SHARED_OPAQUE_HANDLE = 1,
	RTAIME_MEDIA_IO_PINNED_HOST_LEASE = 2,
	RTAIME_MEDIA_IO_DEVICE_DIRECT_LEASE = 3
} rtaime_media_io_transfer_mode;

typedef enum rtaime_media_io_pixel_format
{
	RTAIME_MEDIA_IO_RGBA8 = 1,
	RTAIME_MEDIA_IO_UYVY8 = 2,
	RTAIME_MEDIA_IO_V210 = 3
} rtaime_media_io_pixel_format;

typedef struct rtaime_media_io_identity
{
	uint8_t bytes[RTAIME_MEDIA_IO_IDENTITY_BYTES];
} rtaime_media_io_identity;

typedef struct rtaime_media_io_video_format
{
	uint32_t width;
	uint32_t height;
	uint32_t frame_rate_numerator;
	uint32_t frame_rate_denominator;
	uint32_t pixel_format;
	uint32_t progressive;
} rtaime_media_io_video_format;

typedef struct rtaime_media_io_port_info
{
	rtaime_media_io_identity port_id;
	rtaime_media_io_identity resource_id;
	uint32_t direction;
	uint32_t transport;
	uint32_t supports_external_reference;
} rtaime_media_io_port_info;

typedef struct rtaime_media_io_session_config
{
	uint32_t abi_version_major;
	uint32_t abi_version_minor;
	rtaime_media_io_identity port_id;
	uint32_t direction;
	rtaime_media_io_video_format normalized_format;
	uint32_t transfer_mode;
	uint32_t embedded_audio_enabled;
	uint32_t external_reference_required;
} rtaime_media_io_session_config;

/// <summary>
/// Descriptor for a provider-owned buffer. opaque_handle identifies vendor/OS/device memory through an adapter;
/// it is never a pointer to media bytes that management/control IPC may serialize. lease_id must be released once.
/// </summary>
typedef struct rtaime_media_io_buffer_lease
{
	rtaime_media_io_identity lease_id;
	uint64_t opaque_handle;
	uint64_t byte_length;
	uint32_t row_bytes;
	uint32_t transfer_mode;
	uint32_t storage_domain;
	uint32_t native_pixel_format;
} rtaime_media_io_buffer_lease;

typedef struct rtaime_media_io_input_frame
{
	rtaime_media_io_identity port_id;
	rtaime_media_io_identity source_id;
	uint64_t sequence_number;
	int64_t presentation_timestamp;
	uint32_t timebase_numerator;
	uint32_t timebase_denominator;
	rtaime_media_io_video_format normalized_format;
	rtaime_media_io_buffer_lease video;
	uint64_t audio_opaque_handle;
	uint32_t audio_sample_count;
	uint32_t audio_channel_count;
} rtaime_media_io_input_frame;

typedef struct rtaime_media_io_output_frame
{
	rtaime_media_io_identity port_id;
	uint64_t sequence_number;
	int64_t presentation_timestamp;
	uint32_t timebase_numerator;
	uint32_t timebase_denominator;
	rtaime_media_io_video_format format;
	uint64_t opaque_surface_handle;
	rtaime_media_io_identity surface_lease_id;
	uint64_t audio_opaque_handle;
	uint32_t audio_sample_count;
	uint32_t audio_channel_count;
} rtaime_media_io_output_frame;

typedef struct rtaime_media_io_port_status
{
	rtaime_media_io_identity port_id;
	uint32_t signal_state;
	int64_t observed_at_unix_milliseconds;
	int32_t vendor_status_code;
} rtaime_media_io_port_status;

rtaime_media_io_result rtaime_media_io_create_provider(
	uint32_t abi_version_major,
	uint32_t abi_version_minor,
	rtaime_media_io_provider** provider);

void rtaime_media_io_destroy_provider(rtaime_media_io_provider* provider);

rtaime_media_io_result rtaime_media_io_get_port_count(
	rtaime_media_io_provider* provider,
	uint32_t* port_count);

rtaime_media_io_result rtaime_media_io_get_port(
	rtaime_media_io_provider* provider,
	uint32_t index,
	rtaime_media_io_port_info* port);

rtaime_media_io_result rtaime_media_io_open_session(
	rtaime_media_io_provider* provider,
	const rtaime_media_io_session_config* config,
	rtaime_media_io_session** session);

void rtaime_media_io_close_session(rtaime_media_io_session* session);

rtaime_media_io_result rtaime_media_io_get_status(
	rtaime_media_io_session* session,
	rtaime_media_io_port_status* status);

rtaime_media_io_result rtaime_media_io_input_try_acquire(
	rtaime_media_io_session* session,
	rtaime_media_io_input_frame* frame);

rtaime_media_io_result rtaime_media_io_input_release(
	rtaime_media_io_session* session,
	const rtaime_media_io_identity* lease_id);

rtaime_media_io_result rtaime_media_io_output_try_submit(
	rtaime_media_io_session* session,
	const rtaime_media_io_output_frame* frame);

#ifdef __cplusplus
}
#endif
