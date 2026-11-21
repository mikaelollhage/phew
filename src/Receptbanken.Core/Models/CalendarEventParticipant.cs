namespace Receptbanken.Core.Models;

/// <summary>
/// Join entity linking a <see cref="CalendarEvent"/> to a registered <see cref="FamilyMember"/>.
/// </summary>
public class CalendarEventParticipant
{
    public int CalendarEventId { get; set; }
    public CalendarEvent? CalendarEvent { get; set; }

    public int FamilyMemberId { get; set; }
    public FamilyMember? FamilyMember { get; set; }
}
