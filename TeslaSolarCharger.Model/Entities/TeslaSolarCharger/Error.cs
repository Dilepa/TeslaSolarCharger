﻿namespace TeslaSolarCharger.Model.Entities.TeslaSolarCharger;

public class LoggedError
{
    public int Id { get; set; }
    public DateTime StartTimeStamp { get; set; }
    public DateTime? EndTimeStamp { get; set; }
    public List<DateTime> FurtherOccurrences { get; set; } = new();
    public string IssueKey { get; set; }
    public string Headline {get; set; }
    public string? Vin { get; set; }
    public string Source { get; set; }
    public string MethodName { get; set; }
    public string Message { get; set; }
    public string? StackTrace { get; set; }
    public DateTime? DismissedAt { get; set; }
    public bool TelegramNotificationSent { get; set; }
    public bool TelegramResolvedMessageSent { get; set; }
}
