// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Reflection;
using rtaime.Provider.Srt;
using Xunit;

namespace rtaime.Tests.Unit;

public sealed class SrtMediaFoundationCleanupTests
{
	[Fact]
	public void Partial_initialization_cleanup_preserves_order_and_skips_unowned_resources()
	{
		var released = new List<string>();
		var failure = Release(null,
			() => released.Add("writer"),
			null,
			() => released.Add("media-sink-shutdown"),
			() => released.Add("media-sink-reference"),
			() => released.Add("byte-stream"),
			() => released.Add("backing-stream"),
			() => released.Add("mf-shutdown"));
		Assert.Null(failure);
		Assert.Equal(new[] { "writer", "media-sink-shutdown", "media-sink-reference", "byte-stream", "backing-stream", "mf-shutdown" }, released);
	}

	[Fact]
	public void Shutdown_failures_do_not_prevent_remaining_resource_release_and_all_failures_are_returned()
	{
		var released = new List<string>();
		var writerFailure = new IOException("writer release failed");
		var shutdownFailure = new IOException("media sink shutdown failed");
		var failure = Release(null,
			() => { released.Add("writer"); throw writerFailure; },
			() => { released.Add("media-sink-shutdown"); throw shutdownFailure; },
			() => released.Add("media-sink-reference"),
			() => released.Add("byte-stream"),
			() => released.Add("backing-stream"),
			() => released.Add("mf-shutdown"));
		Assert.Equal(new[] { "writer", "media-sink-shutdown", "media-sink-reference", "byte-stream", "backing-stream", "mf-shutdown" }, released);
		var aggregate = Assert.IsType<AggregateException>(failure);
		Assert.Equal(new Exception[] { writerFailure, shutdownFailure }, aggregate.InnerExceptions);
	}

	[Fact]
	public void Original_initialization_failure_survives_multiple_cleanup_phases_with_secondary_failures()
	{
		var primary = new InvalidOperationException("mux initialization failed");
		var typeFailure = new IOException("temporary media type release failed");
		var sinkFailure = new IOException("media sink shutdown failed");
		var released = 0;
		var failure = Release(primary, () => { released++; throw typeFailure; });
		failure = Release(failure, () => { released++; throw sinkFailure; }, () => released++);
		Assert.Same(primary, failure);
		Assert.Equal(3, released);
		var cleanup = Assert.IsType<AggregateException>(primary.Data["MediaFoundationCleanupFailure"]);
		Assert.Contains(typeFailure, cleanup.Flatten().InnerExceptions);
		Assert.Contains(sinkFailure, cleanup.Flatten().InnerExceptions);
	}

	private static Exception? Release(Exception? primary, params Action?[] releases)
	{
		var type = typeof(WindowsMediaFoundationSrtEncoder).Assembly.GetType("rtaime.Provider.Srt.MediaFoundationNetwork", throwOnError: true)!;
		return (Exception?)type.GetMethod("ReleaseResources", BindingFlags.Static | BindingFlags.Public)!.Invoke(null, [primary, releases]);
	}
}
