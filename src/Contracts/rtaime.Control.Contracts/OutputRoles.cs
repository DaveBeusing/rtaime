// Copyright (c) 2026 Dave Beusing
// david.beusing@gmail.com

namespace rtaime.Control.Contracts;

public readonly record struct OutputRoleId
{
	public OutputRoleId(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
			throw new ArgumentException("Output role identity is required.", nameof(value));
		var normalized = value.Trim().ToLowerInvariant();
		if (normalized.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
			throw new ArgumentException("Output role identity may contain only ASCII letters, digits and hyphens.", nameof(value));
		Value = normalized;
	}

	public string Value { get; }
	public override string ToString() => Value;
}

public static class OutputRoleIds
{
	public static OutputRoleId Program { get; } = new("program");
	public static OutputRoleId Aux { get; } = new("aux");
}

public enum OutputRoleKind
{
	Program = 1,
	Aux = 2
}

public sealed record ProductionOutputRoleState
{
	public ProductionOutputRoleState(
		OutputRoleId roleId,
		OutputRoleKind kind,
		ProductionSourceId sourceId,
		string providerSelector,
		string targetId,
		string formatPolicy = "production",
		string timingPolicy = "production",
		bool enabled = true)
	{
		if (!Enum.IsDefined(typeof(OutputRoleKind), kind))
			throw new ArgumentOutOfRangeException(nameof(kind), "Output role kind must be a defined contract value.");
		if (string.IsNullOrWhiteSpace(providerSelector))
			throw new ArgumentException("Output provider selector is required.", nameof(providerSelector));
		if (string.IsNullOrWhiteSpace(targetId))
			throw new ArgumentException("Output target identity is required.", nameof(targetId));
		if (string.IsNullOrWhiteSpace(formatPolicy))
			throw new ArgumentException("Output format policy is required.", nameof(formatPolicy));
		if (string.IsNullOrWhiteSpace(timingPolicy))
			throw new ArgumentException("Output timing policy is required.", nameof(timingPolicy));

		RoleId = roleId;
		Kind = kind;
		SourceId = sourceId;
		ProviderSelector = providerSelector.Trim();
		TargetId = targetId.Trim();
		FormatPolicy = formatPolicy.Trim();
		TimingPolicy = timingPolicy.Trim();
		Enabled = enabled;
	}

	public OutputRoleId RoleId { get; }
	public OutputRoleKind Kind { get; }
	public ProductionSourceId SourceId { get; }
	public string ProviderSelector { get; }
	public string TargetId { get; }
	public string FormatPolicy { get; }
	public string TimingPolicy { get; }
	public bool Enabled { get; }

	public ProductionOutputRoleState WithSource(ProductionSourceId sourceId) =>
		new(RoleId, Kind, sourceId, ProviderSelector, TargetId, FormatPolicy, TimingPolicy, Enabled);

	public static ProductionOutputRoleState Program(ProductionSourceId sourceId) =>
		new(OutputRoleIds.Program, OutputRoleKind.Program, sourceId, "auto", "program");

	public static ProductionOutputRoleState Aux(ProductionSourceId sourceId) =>
		new(OutputRoleIds.Aux, OutputRoleKind.Aux, sourceId, "auto", "aux");
}

internal sealed class OutputRoleStateCollection : IReadOnlyList<ProductionOutputRoleState>, IEquatable<OutputRoleStateCollection>
{
	private readonly ProductionOutputRoleState[] _roles;

	private OutputRoleStateCollection(ProductionOutputRoleState[] roles) =>
		_roles = roles;

	public static OutputRoleStateCollection Normalize(
		IReadOnlyList<ProductionOutputRoleState>? roles,
		ProductionSourceId programSourceId)
	{
		var snapshot = roles?.ToArray() ?? new[] { ProductionOutputRoleState.Program(programSourceId) };
		if (snapshot.Length == 0)
			throw new ArgumentException("At least the Program output role is required.", nameof(roles));
		if (snapshot.Any(role => role is null))
			throw new ArgumentException("Output roles must not contain null values.", nameof(roles));
		if (snapshot.Select(role => role.RoleId).Distinct().Count() != snapshot.Length)
			throw new ArgumentException("Output role identities must be unique.", nameof(roles));
		return new OutputRoleStateCollection(snapshot);
	}

	public int Count => _roles.Length;
	public ProductionOutputRoleState this[int index] => _roles[index];

	public IEnumerator<ProductionOutputRoleState> GetEnumerator() =>
		((IEnumerable<ProductionOutputRoleState>)_roles).GetEnumerator();

	System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
		_roles.GetEnumerator();

	public bool Equals(OutputRoleStateCollection? other) =>
		ReferenceEquals(this, other) ||
		(other is not null && _roles.SequenceEqual(other._roles));

	public override bool Equals(object? obj) =>
		obj is OutputRoleStateCollection other && Equals(other);

	public override int GetHashCode()
	{
		var hash = new HashCode();
		foreach (var role in _roles)
			hash.Add(role);
		return hash.ToHashCode();
	}
}

public sealed record RouteOutputRoleCommand
{
	public RouteOutputRoleCommand(ControlCommandMetadata metadata, OutputRoleId roleId, ProductionSourceId sourceId)
	{
		Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
		RoleId = roleId;
		SourceId = sourceId;
	}

	public ControlCommandMetadata Metadata { get; }
	public OutputRoleId RoleId { get; }
	public ProductionSourceId SourceId { get; }
}
