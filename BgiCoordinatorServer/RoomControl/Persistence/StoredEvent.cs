using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BgiCoordinatorServer.RoomControl.Persistence;

[Table("ControlRoomEvents")]
public class StoredEvent
{
    [Key]
    public Guid EventId { get; set; }

    [Required]
    [MaxLength(64)]
    public string AggregateId { get; set; } = "";

    public long Version { get; set; }

    [Required]
    [MaxLength(128)]
    public string EventType { get; set; } = "";

    [Required]
    public string Payload { get; set; } = "";

    public DateTime Timestamp { get; set; }

    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long? SequenceNumber { get; set; }
}
