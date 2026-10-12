// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using Vortice.Direct3D11;
using Vortice.DXGI;

namespace rtaime.Operator.Controls;

internal readonly record struct OperatorSurfaceTarget(int Width, int Height);

internal static class OperatorSurfaceTargetPolicy
{
	public static bool TryMeasure(double widthDip, double heightDip, double scaleX, double scaleY,
		int maximumWidth, int maximumHeight, out OperatorSurfaceTarget target, out string reason)
	{
		target = default;
		if (!double.IsFinite(widthDip) || !double.IsFinite(heightDip) ||
			!double.IsFinite(scaleX) || !double.IsFinite(scaleY) ||
			widthDip <= 0 || heightDip <= 0 || scaleX <= 0 || scaleY <= 0)
		{
			reason = "transient-or-nonfinite-bounds";
			return false;
		}
		// Cover the available physical viewport; never truncate fractional pixels or clamp
		// an unsupported target to an arbitrary smaller preview resolution.
		var width = Math.Ceiling(widthDip * scaleX);
		var height = Math.Ceiling(heightDip * scaleY);
		if (!double.IsFinite(width) || !double.IsFinite(height) ||
			width < 1 || height < 1 || width > maximumWidth || height > maximumHeight || maximumWidth <= 0 || maximumHeight <= 0)
		{
			reason = "device-or-interop-limit";
			return false;
		}
		target = new OperatorSurfaceTarget(checked((int)width), checked((int)height));
		reason = "valid";
		return true;
	}

	public static Texture2DDescription Describe(OperatorSurfaceTarget target) => new()
	{
		Width = checked((uint)target.Width), Height = checked((uint)target.Height),
		MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
		SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
		BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
		CPUAccessFlags = CpuAccessFlags.None, MiscFlags = ResourceOptionFlags.Shared
	};

	public static bool IsValid(Texture2DDescription description, int maximumWidth, int maximumHeight) =>
		description.Width > 0 && description.Height > 0 &&
		description.Width <= maximumWidth && description.Height <= maximumHeight &&
		description.MipLevels == 1 && description.ArraySize == 1 &&
		description.Format == Format.B8G8R8A8_UNorm &&
		description.SampleDescription.Count == 1 && description.SampleDescription.Quality == 0 &&
		description.Usage == ResourceUsage.Default &&
		description.BindFlags == (BindFlags.RenderTarget | BindFlags.ShaderResource) &&
		description.CPUAccessFlags == CpuAccessFlags.None &&
		description.MiscFlags == ResourceOptionFlags.Shared;
}
