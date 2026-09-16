// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;

namespace rtaime.Tests.Unit;

public sealed class LocalEndpointLeaseTests
{
	[Fact]
	public void Lease_is_visible_until_owner_disposes_it()
	{
		var endpoint = $"rtaime.test.lease.{Guid.NewGuid():N}";
		Assert.False(LocalEndpointLease.IsHeld(endpoint));

		using (var lease = LocalEndpointLease.Acquire(endpoint))
		{
			Assert.Equal(endpoint, lease.Endpoint);
			Assert.True(LocalEndpointLease.IsHeld(endpoint));
			Assert.Throws<InvalidOperationException>(() => LocalEndpointLease.Acquire(endpoint));
		}

		Assert.False(LocalEndpointLease.IsHeld(endpoint));
	}

	[Fact]
	public void Readiness_is_distinct_from_process_lifetime_lease()
	{
		var endpoint = $"rtaime.test.readiness.{Guid.NewGuid():N}";
		Assert.False(LocalEndpointLease.IsHeld(endpoint));
		Assert.False(LocalEndpointReadinessLease.IsHeld(endpoint));

		using var lifetime = LocalEndpointLease.Acquire(endpoint);
		Assert.True(LocalEndpointLease.IsHeld(endpoint));
		Assert.False(LocalEndpointReadinessLease.IsHeld(endpoint));

		using (var readiness = LocalEndpointReadinessLease.Acquire(endpoint))
		{
			Assert.Equal(endpoint, readiness.Endpoint);
			Assert.True(LocalEndpointLease.IsHeld(endpoint));
			Assert.True(LocalEndpointReadinessLease.IsHeld(endpoint));
			Assert.Throws<InvalidOperationException>(() => LocalEndpointReadinessLease.Acquire(endpoint));
		}

		Assert.True(LocalEndpointLease.IsHeld(endpoint));
		Assert.False(LocalEndpointReadinessLease.IsHeld(endpoint));
	}

	[Fact]
	public void Independent_endpoints_can_be_leased_concurrently()
	{
		var firstEndpoint = $"rtaime.test.lease.{Guid.NewGuid():N}";
		var secondEndpoint = $"rtaime.test.lease.{Guid.NewGuid():N}";

		using var first = LocalEndpointLease.Acquire(firstEndpoint);
		using var second = LocalEndpointLease.Acquire(secondEndpoint);

		Assert.True(LocalEndpointLease.IsHeld(firstEndpoint));
		Assert.True(LocalEndpointLease.IsHeld(secondEndpoint));
	}
}
