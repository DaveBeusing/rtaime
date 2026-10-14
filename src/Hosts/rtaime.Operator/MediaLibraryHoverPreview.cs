// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Windows.Media;

namespace rtaime.Operator;

public enum MediaHoverCapability { Unavailable, ExistingThumbnail, Offline }
public sealed record MediaHoverPreview(string Key, ImageSource? Thumbnail, string Status);

// Admission is latest-only and has no media, file, decoder, renderer or control transport dependency.
// Existing monitoring images are shared without copying or retaining an asset cache.
public sealed class MediaLibraryHoverPreview : IDisposable
{
	private CancellationTokenSource? _pending;
	private long _generation;
	private bool _disposed;
	public static MediaHoverCapability Capability(MediaPoolItemViewModel item) =>
		!item.IsOnline ? MediaHoverCapability.Offline :
		item.Thumbnail is null ? MediaHoverCapability.Unavailable : MediaHoverCapability.ExistingThumbnail;

	public async Task<MediaHoverPreview?> RequestAsync(MediaPoolItemViewModel item, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(item);
		if (_disposed) return null;
		Cancel();
		var generation = _generation;
		using var pending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		_pending = pending;
		try
		{
			await Task.Delay(180, pending.Token);
			if (_disposed || generation != _generation || pending.IsCancellationRequested) return null;
			return Capability(item) switch
			{
				MediaHoverCapability.Offline => new(item.Key, null, "OFFLINE · hover preview unavailable"),
				MediaHoverCapability.ExistingThumbnail => new(item.Key, item.Thumbnail, "MONITOR THUMBNAIL · scrubbing unsupported"),
				_ => new(item.Key, null, "UNSUPPORTED · no independent thumbnail or scrub capability")
			};
		}
		catch (OperationCanceledException) when (pending.IsCancellationRequested) { return null; }
		finally { if (ReferenceEquals(_pending, pending)) _pending = null; }
	}
	public void Cancel()
	{
		_generation++;
		_pending?.Cancel();
		_pending = null;
	}
	public void Dispose() { _disposed = true; Cancel(); }
}
