using rtaime.Core;
using rtaime.Runtime.Contracts;

namespace rtaime.Runtime;

public sealed class InMemoryRuntimeResourceReservationManager : IRuntimeResourceReservationManager
{
    private readonly object _gate = new();
    private readonly Dictionary<Identity, PreparedExecutionId> _reservations = new();

    public RuntimeResourceReservationResult Reserve(PreparedExecutionContract preparedExecution)
    {
        ArgumentNullException.ThrowIfNull(preparedExecution);

        lock (_gate)
        {
            var resourceKey = string.Join(
                ";",
                preparedExecution.Bindings
                    .Select(binding => binding.Resource.ResourceId.ToString())
                    .OrderBy(value => value, StringComparer.Ordinal));

            var reservationId = RuntimeIdentity.Create(
                "runtime-in-memory-reservation",
                preparedExecution.PreparedExecutionId.ToString(),
                resourceKey);

            if (_reservations.ContainsKey(reservationId))
            {
                return RuntimeResourceReservationResult.Rejected(new Failure(
                    "runtime.reservation.identity_conflict",
                    "The deterministic in-memory reservation identity is already active."));
            }

            _reservations.Add(reservationId, preparedExecution.PreparedExecutionId);
            return RuntimeResourceReservationResult.Reserved(reservationId);
        }
    }

    public RuntimeResourceReleaseResult Release(Identity reservationId)
    {
        if (reservationId.IsEmpty)
            throw new ArgumentException("Reservation identity must not be empty.", nameof(reservationId));

        lock (_gate)
        {
            if (_reservations.Remove(reservationId))
                return RuntimeResourceReleaseResult.Released();

            return RuntimeResourceReleaseResult.Rejected(new Failure(
                "runtime.reservation.unknown",
                "The in-memory reservation identity is not active."));
        }
    }
}
