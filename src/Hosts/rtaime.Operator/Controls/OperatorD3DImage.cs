// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
// WPF shared-surface interop follows Vortice.Windows (MIT); see docs/VorticeWpfInteropLicense.txt.

using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Vortice.Direct3D11;
using Vortice.Direct3D9;
using Vortice.DXGI;
using Dx9Format = Vortice.Direct3D9.Format;
using Dx9Usage = Vortice.Direct3D9.Usage;
using static Vortice.Direct3D9.D3D9;

namespace rtaime.Operator.Controls;

// D3DImage must be locked while the GPU writes, and detached before either API's
// texture is disposed. All leases and callbacks belong to one WPF Dispatcher.
internal sealed class OperatorD3DImage : D3DImage, IDisposable
{
	private static readonly ConcurrentDictionary<Dispatcher, DeviceLease> Leases = [];
	private readonly DeviceLease _lease;
	private IDirect3DTexture9? _texture;
	private bool _disposed;

	public int MaximumWidth => checked((int)_lease.Capabilities.MaxTextureWidth);
	public int MaximumHeight => checked((int)_lease.Capabilities.MaxTextureHeight);

	public OperatorD3DImage(Window window)
	{
		Dispatcher.VerifyAccess();
		if (!Leases.TryGetValue(Dispatcher, out var lease))
		{
			lease = new DeviceLease(window);
			if (!Leases.TryAdd(Dispatcher, lease))
				throw new InvalidOperationException("A dispatcher already owns the D3D9 interop lease.");
		}
		_lease = lease;
		_lease.Users++;
	}

	public void Bind(ID3D11Texture2D? texture)
	{
		Dispatcher.VerifyAccess();
		ObjectDisposedException.ThrowIf(_disposed, this);
		IDirect3DTexture9? candidate = null;
		if (texture is not null)
		{
			using var resource = texture.QueryInterface<IDXGIResource>();
			var handle = resource.SharedHandle;
			if (handle == IntPtr.Zero)
				throw new InvalidOperationException("Shared BGRA target has no DXGI interop handle.");
			candidate = _lease.Device.CreateTexture(texture.Description.Width, texture.Description.Height,
				1, Dx9Usage.RenderTarget, Dx9Format.A8R8G8B8, Pool.Default, ref handle);
		}
		try
		{
			Lock();
			try
			{
				using var surface = candidate?.GetSurfaceLevel(0);
				SetBackBuffer(D3DResourceType.IDirect3DSurface9, surface?.NativePointer ?? IntPtr.Zero);
			}
			finally { Unlock(); }
			var previous = _texture;
			_texture = candidate;
			candidate = null;
			previous?.Dispose();
		}
		finally { candidate?.Dispose(); }
	}

	public bool BeginDraw()
	{
		Dispatcher.VerifyAccess();
		return !_disposed && _texture is not null && IsFrontBufferAvailable &&
			TryLock(new Duration(TimeSpan.Zero));
	}

	public void EndDraw(bool changed)
	{
		try
		{
			if (changed && PixelWidth > 0 && PixelHeight > 0)
				AddDirtyRect(new Int32Rect(0, 0, PixelWidth, PixelHeight));
		}
		finally { Unlock(); }
	}

	public void Dispose()
	{
		Dispatcher.VerifyAccess();
		if (_disposed) return;
		try { Bind(null); }
		finally
		{
			_texture?.Dispose();
			_texture = null;
			_disposed = true;
			if (--_lease.Users == 0)
			{
				Leases.TryRemove(Dispatcher, out _);
				_lease.Dispose();
			}
		}
		GC.SuppressFinalize(this);
	}

	private sealed class DeviceLease : IDisposable
	{
		private readonly IDirect3D9Ex _context;
		public IDirect3DDevice9Ex Device { get; }
		public Capabilities Capabilities { get; }
		public int Users { get; set; }

		public DeviceLease(Window window)
		{
			_context = Direct3DCreate9Ex();
			try
			{
				Capabilities = _context.GetDeviceCaps(0, DeviceType.Hardware);
				var parameters = new PresentParameters
				{
					Windowed = true, SwapEffect = SwapEffect.Discard,
					DeviceWindowHandle = new WindowInteropHelper(window).EnsureHandle(),
					PresentationInterval = PresentInterval.Default
				};
				Device = _context.CreateDeviceEx(0, DeviceType.Hardware, IntPtr.Zero,
					CreateFlags.HardwareVertexProcessing | CreateFlags.Multithreaded | CreateFlags.FpuPreserve,
					parameters);
			}
			catch { _context.Dispose(); throw; }
		}

		public void Dispose()
		{
			try { Device.Dispose(); }
			finally { _context.Dispose(); }
		}
	}
}
