// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Text.Json;

namespace rtaime.IntegrationHost;

/// <summary>
/// Non-authoritative IS-05 v1.2 test endpoint. Physical/virtual transport admission is absent.
/// Staging is limited to disabled connections. No PATCH can start RTP, play media or route Program.
/// </summary>
internal sealed class NmosVirtualConnections
{
	private readonly object _gate = new();
	private readonly string _senderId;
	private readonly string _receiverId;
	private ulong _revision;

	public NmosVirtualConnections(string senderId, string receiverId)
	{
		_senderId = Guid.Parse(senderId).ToString("D");
		_receiverId = Guid.Parse(receiverId).ToString("D");
	}

	public ulong Revision { get { lock (_gate) return _revision; } }

	public bool Exists(bool sender, string id) =>
		string.Equals(sender ? _senderId : _receiverId, id, StringComparison.Ordinal);

	public object Read(bool sender, string id, bool active)
	{
		if (!Exists(sender, id))
			throw new KeyNotFoundException("NMOS resource is not registered in the virtual connection store.");
		lock (_gate) return State(sender, immediate: false);
	}

	public object Constraints(bool sender, string id)
	{
		if (!Exists(sender, id)) throw new KeyNotFoundException();
		// The virtual transport has no admitted interfaces, RTP parameters or SDP transport file.
		return new object[] { new Dictionary<string, object>() };
	}

	public NmosMutationOutcome Stage(bool sender, string id, JsonElement patch)
	{
		lock (_gate)
		{
			if (!Exists(sender, id))
				return new(404, null, "NMOS virtual sender/receiver is not registered.");
			var validation = Validate(sender, patch);
			if (validation is not null)
				return new(validation.Value, null, "No admitted RTP provider: only disabled virtual staging and immediate disable are permitted.");
			var immediate = ActivationIsImmediate(patch);
			_revision++;
			return new(200, State(sender, immediate), "VIRTUAL_DISABLED");
		}
	}

	public NmosMutationOutcome StageBulk(bool sender, JsonElement bulk)
	{
		lock (_gate)
		{
			if (bulk.ValueKind != JsonValueKind.Array || bulk.GetArrayLength() is < 1 or > 16)
				return new(400, null, "NMOS bulk body must contain 1..16 connections.");
			var ids = new HashSet<string>(StringComparer.Ordinal);
			var requests = new List<(string Id, bool Immediate)>();
			foreach (var entry in bulk.EnumerateArray())
			{
				if (entry.ValueKind != JsonValueKind.Object ||
					!entry.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String ||
					!entry.TryGetProperty("params", out var patch) ||
					entry.EnumerateObject().Any(property => property.Name is not ("id" or "params")))
					return new(400, null, "Malformed IS-05 bulk entry.");
				var id = idValue.GetString()!;
				if (!Exists(sender, id) || !ids.Add(id))
					return new(409, null, "Unknown or duplicate IS-05 bulk sender/receiver identity.");
				var invalid = Validate(sender, patch);
				if (invalid is not null)
					return new(invalid.Value, null, "No permitted virtual activation or transport parameters.");
				requests.Add((id, ActivationIsImmediate(patch)));
			}
			// All-or-none validation before any staged observation revision advances.
			_revision++;
			var response = requests.Select(entry =>
				(object)new { id = entry.Id, @params = State(sender, entry.Immediate) }).ToArray();
			return new(200, response, "VIRTUAL_DISABLED");
		}
	}

	private static int? Validate(bool sender, JsonElement patch)
	{
		if (patch.ValueKind != JsonValueKind.Object)
			return 400;
		foreach (var field in patch.EnumerateObject())
		{
			if (field.Name is not ("sender_id" or "receiver_id" or "master_enable" or "activation" or "transport_params" or "transport_file"))
				return 400;
			if (field.Name == (sender ? "sender_id" : "receiver_id"))
				return 400;
			if (field.Name == (sender ? "receiver_id" : "sender_id") &&
				field.Value.ValueKind != JsonValueKind.Null)
				return 409;
			if (field.Name == "master_enable" && field.Value.ValueKind != JsonValueKind.False)
				return 409;
			if (field.Name == "transport_file" && !NullTransportFile(field.Value))
				return 409;
			if (field.Name == "transport_params" &&
				(field.Value.ValueKind != JsonValueKind.Array ||
				 field.Value.GetArrayLength() != 1 ||
				 field.Value[0].ValueKind != JsonValueKind.Object ||
				 field.Value[0].EnumerateObject().Any()))
				return 409;
			if (field.Name == "activation")
			{
				if (field.Value.ValueKind != JsonValueKind.Object)
					return 400;
				if (!field.Value.TryGetProperty("mode", out var mode) ||
					!(mode.ValueKind == JsonValueKind.Null ||
					  (mode.ValueKind == JsonValueKind.String && mode.GetString() == "activate_immediate")) ||
					field.Value.EnumerateObject().Any(item => item.Name is not ("mode" or "requested_time")) ||
					(field.Value.TryGetProperty("requested_time", out var time) && time.ValueKind != JsonValueKind.Null))
					return 409; // Scheduled activation requires real clocks and admitted provider support.
			}
		}
		return null;
	}

	private static bool NullTransportFile(JsonElement value)
	{
		if (value.ValueKind == JsonValueKind.Null) return true;
		if (value.ValueKind != JsonValueKind.Object ||
			!value.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.Null ||
			!value.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Null)
			return false;
		return !value.EnumerateObject().Any(property => property.Name is not ("type" or "data"));
	}

	private static bool ActivationIsImmediate(JsonElement patch) =>
		patch.TryGetProperty("activation", out var activation) &&
		activation.ValueKind == JsonValueKind.Object &&
		activation.TryGetProperty("mode", out var mode) &&
		mode.ValueKind == JsonValueKind.String &&
		mode.GetString() == "activate_immediate";

	private static object State(bool sender, bool immediate)
	{
		var activation = new { mode = immediate ? "activate_immediate" : (string?)null,
			requested_time = (string?)null, activation_time = (string?)null };
		var legs = new object[] { new Dictionary<string, object>() };
		if (sender)
			return new { receiver_id = (string?)null, master_enable = false, activation, transport_params = legs };
		return new { sender_id = (string?)null, master_enable = false, activation, transport_params = legs,
			transport_file = new { data = (string?)null, type = (string?)null } };
	}
}

internal sealed record NmosMutationOutcome(int HttpStatus, object? Result, string Detail);
