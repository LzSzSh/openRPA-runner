using OpenRpaWorkflowLauncher.ViewModels;
using System.Text.Json.Serialization;

namespace OpenRpaWorkflowLauncher.Models;

public sealed class ScheduledAutomation : ObservableObject
{
    private DateTime? _lastRunAt;
    private string _lastRunStatus = "等待执行";

    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProjectName { get; set; } = string.Empty;
    public string ProjectFolder { get; set; } = string.Empty;
    // Kept for backward-compatible loading of schedules created by the previous UI.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkflowName { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkflowSourceFile { get; set; }
    public string ScheduleType { get; set; } = "Weekly";
    public List<DayOfWeek> Weekdays { get; set; } = [];
    public int? DayOfMonth { get; set; }
    public int Hour { get; set; }
    public int Minute { get; set; }

    public DateTime? LastRunAt
    {
        get => _lastRunAt;
        set
        {
            if (SetProperty(ref _lastRunAt, value))
            {
                OnPropertyChanged(nameof(LastRunText));
            }
        }
    }

    public string LastRunStatus
    {
        get => _lastRunStatus;
        set => SetProperty(ref _lastRunStatus, value);
    }

    [JsonIgnore]
    public string ProjectDisplay => ProjectName;
    [JsonIgnore]
    public string TimeText => $"{Hour:00}:{Minute:00}";
    [JsonIgnore]
    public string ScheduleText => ScheduleType == "Monthly"
        ? $"每月 {DayOfMonth} 日 {TimeText}"
        : $"每周{string.Join("、", Weekdays.OrderBy(GetWeekdayOrder).Select(GetWeekdayName))} {TimeText}";
    [JsonIgnore]
    public string LastRunText => LastRunAt.HasValue ? LastRunAt.Value.ToString("yyyy-MM-dd HH:mm") : "尚未执行";

    public bool IsDue(DateTime now, TimeSpan catchUpWindow)
    {
        DateTime? latestOccurrence = new[] { now.Date, now.Date.AddDays(-1) }
            .Where(ScheduleMatchesDate)
            .Select(date => date.AddHours(Hour).AddMinutes(Minute))
            .Where(candidate => candidate <= now)
            .OrderByDescending(candidate => candidate)
            .Cast<DateTime?>()
            .FirstOrDefault();
        if (!latestOccurrence.HasValue || now - latestOccurrence.Value > catchUpWindow) return false;
        return !LastRunAt.HasValue || LastRunAt.Value < latestOccurrence.Value;
    }

    private bool ScheduleMatchesDate(DateTime date) => ScheduleType == "Monthly"
        ? DayOfMonth == date.Day
        : Weekdays.Contains(date.DayOfWeek);

    private static int GetWeekdayOrder(DayOfWeek day) => day == DayOfWeek.Sunday ? 7 : (int)day;

    private static string GetWeekdayName(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "一",
        DayOfWeek.Tuesday => "二",
        DayOfWeek.Wednesday => "三",
        DayOfWeek.Thursday => "四",
        DayOfWeek.Friday => "五",
        DayOfWeek.Saturday => "六",
        _ => "日"
    };
}
