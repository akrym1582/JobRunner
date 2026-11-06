using JobRunner.Models;

namespace JobRunner.Controllers.Ui;

/// <summary>月間の確定予定と実績です。</summary>
/// <param name="Month">Monthです。</param>
/// <param name="TimeZone">TimeZoneです。</param>
/// <param name="Units">Unitsです。</param>
/// <param name="Entries">Entriesです。</param>
public sealed record MonthlySchedule(string Month, string TimeZone, List<MonthlyUnit> Units, List<MonthlyEntry> Entries);
