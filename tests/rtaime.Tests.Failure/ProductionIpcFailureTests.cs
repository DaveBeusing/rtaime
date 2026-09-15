// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.ControlHost;

namespace rtaime.Tests.Failure;

public sealed class ProductionIpcFailureTests
{
	[Fact]
	public async Task Missing_RuntimeHost_times_out_without_marking_transport_connected()
	{
		var transport = new NamedPipeRuntimeHostTransport(
			$"rtaime.test.missing.{Guid.NewGuid():N}",
			TimeSpan.FromMilliseconds(100),
			TimeSpan.FromMilliseconds(300));

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.ConnectAsync().AsTask());

		Assert.False(transport.IsConnected);
		Assert.Null(transport.HostInstanceId);
		Assert.Empty(transport.ProviderDescriptors);
	}
}
