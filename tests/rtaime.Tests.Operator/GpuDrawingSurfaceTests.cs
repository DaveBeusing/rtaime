// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Reflection;
using System.Windows.Threading;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;
using Xunit;

namespace rtaime.Tests.Operator;

[Collection("Operator application")]
public sealed class GpuDrawingSurfaceTests
{
	internal static void VerifyDescriptorBoundary(Dispatcher dispatcher)
	{
		dispatcher.VerifyAccess();
		D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
			FeatureLevel.Level_11_0, out ID3D11Device? device, out ID3D11DeviceContext? context).CheckError();
		using (device)
		using (context)
		{
			Assert.NotNull(device);
			var descriptor = new Texture2DDescription
			{
				Width = uint.MaxValue, Height = 100, MipLevels = 1, ArraySize = 1,
				Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
				Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
				CPUAccessFlags = CpuAccessFlags.None, MiscFlags = ResourceOptionFlags.Shared
			};
			var failure = Assert.Throws<SharpGenException>(() => device.CreateTexture2D(descriptor));
			Assert.Equal(unchecked((int)0x80070057), failure.ResultCode.Code);
			var package = typeof(Vortice.Wpf.DrawingSurface).Assembly
				.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
			Console.WriteLine($"GPU DESCRIPTOR REPRO PASS: Hardware D3D11; Vortice.Wpf={package}; Width=4294967295 Height=100 MipLevels=1 ArraySize=1 Format=BGRA8 SampleCount=1 Quality=0 Usage=Default Bind=RenderTarget|ShaderResource CPU=None Misc=Shared HRESULT=0x{failure.ResultCode.Code:X8}. This proves the invalid-width descriptor boundary, not the unrecorded production-session descriptor.");
		}
	}
}
