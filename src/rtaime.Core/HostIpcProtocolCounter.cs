// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Core;

/// <summary>
/// Allocates unsigned protocol-visible values over a signed interlocked backing store without rollover.
/// </summary>
public sealed class HostIpcProtocolCounter
{
	private long _value;

	public HostIpcProtocolCounter(ulong initialValue = 0)
	{
		if (initialValue > long.MaxValue)
			throw new ArgumentOutOfRangeException(nameof(initialValue));
		_value = checked((long)initialValue);
	}

	public ulong Value => checked((ulong)Interlocked.Read(ref _value));

	public bool TryIncrement(out ulong value)
	{
		while (true)
		{
			var current = Interlocked.Read(ref _value);
			if (current == long.MaxValue)
			{
				value = checked((ulong)current);
				return false;
			}

			var next = current + 1;
			if (Interlocked.CompareExchange(ref _value, next, current) == current)
			{
				value = checked((ulong)next);
				return true;
			}
		}
	}
}
