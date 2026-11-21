namespace Receptbanken.Core.Models;

/// <summary>
/// Join entity linking a <see cref="CalendarEventException"/> to a registered <see cref="FamilyMember"/>
/// when participant selection is overridden for a single recurring occurrence.
/// </summary>
public class CalendarEventExceptionParticipant
{
    public int CalendarEventExceptionId { get; set; }
    public CalendarEventException? CalendarEventException { get; set; }

    public int FamilyMemberId { get; set; }
    public FamilyMember? FamilyMember { get; set; }
}
