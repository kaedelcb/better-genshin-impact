using BgiCoordinatorServer.RoomControl.Domain;
using BgiCoordinatorServer.RoomControl.Events;

namespace BgiCoordinatorServer.RoomControl.Persistence;

public interface IControlRoomRepository
{
    Task<ControlRoom?> GetAsync(string roomCode, CancellationToken ct = default);
    Task<ControlRoom?> GetByConnectionIdAsync(string connectionId, CancellationToken ct = default);
    Task<ControlRoom?> GetByPlayerUidAsync(string playerUid, CancellationToken ct = default);
    Task<IReadOnlyList<ControlRoom>> GetAllWithScheduleAsync(CancellationToken ct = default);
    Task SaveAsync(ControlRoom room, IDomainEvent? newEvent = null, CancellationToken ct = default);
    Task DeleteAsync(string roomCode, CancellationToken ct = default);
}
