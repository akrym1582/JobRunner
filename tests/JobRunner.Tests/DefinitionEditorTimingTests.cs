using System.Text;
using JobRunner.Controllers.Ui;
using JobRunner.Editing;
using JobRunner.Models;
using JobRunner.Services;
using JobRunner.Storage;
using Xunit;

namespace JobRunner.Tests;

/// <summary>予定・カレンダーの省略、原文、境界と未登録の試算を確認します。</summary>
public sealed class DefinitionEditorTimingTests
{
    private const string Text = "/* 保持 */\r\nunit=g,0000,owner,team;{ty=g;stt=06:00;sdd=20;md=ne;cl=sa;cl=su;\r\nunit=n;{ty=n;cm=\"残す\";sd=1,2026/11/6;st=1,+01:00;sh=1,no;sy=1,10:00;fd=10;el=child,n,+80 +48;\r\nunit=child;{ty=n;de=n;sd=1,en;ln=1,2;}}}\r\n";

    /// <summary>編集対象外の字句と別ファイル・各文字コードを保持します。</summary>
    /// <param name="encoding">入力の文字コードです。</param>
    /// <param name="bom">BOMを付けるかどうかです。</param>
    [Theory]
    [InlineData("utf-8", false)]
    [InlineData("utf-8", true)]
    [InlineData("utf-16", true)]
    [InlineData("shift_jis", false)]
    public void PreservesLexemesAndAppliesCalendarAlongsideRename(string encoding, bool bom)
    {
        var (service, bundle) = Create(encoding: encoding, bom: bom);
        var draft = DefinitionEditor.Project(bundle, 4);
        Assert.Equal(bundle.Sources.Select(s => s.Base64), DefinitionEditor.Render(bundle, draft).Select(s => s.Base64));
        draft.Roots![0] = draft.Roots[0] with { Node = draft.Roots[0].Node with { Name = "改名" } };
        draft.Schedules![1].Attributes["ln"] = ["1,3"];
        draft.Calendars![0].Attributes["stt"] = ["07:00"];
        var rendered = DefinitionEditor.Render(bundle, draft);
        string expected = Text.Replace("unit=n;", "unit=\"改名\";", StringComparison.Ordinal).Replace("ln=1,2", "ln=1,3", StringComparison.Ordinal).Replace("stt=06:00", "stt=07:00", StringComparison.Ordinal);
        Assert.Equal(Encoding.GetEncoding(encoding).GetBytes((bom ? "\uFEFF" : string.Empty) + expected), Convert.FromBase64String(rendered[0].Base64));
        Assert.Equal(bundle.Sources[1].Base64, rendered[1].Base64);
        Assert.Contains(service.Validate(rendered).Units.Flatten(), u => u.Path == "/g/改名/child" && u.Get("ln") == "1,3");
    }

    /// <summary>旧要求には投影の欠落を削除として適用しません。</summary>
    /// <param name="version">旧投影版です。</param>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PreservesOldRequests(int version)
    {
        var (_, bundle) = Create();
        Assert.Equal(bundle.Sources.Select(s => s.Base64), DefinitionEditor.Render(bundle, DefinitionEditor.Project(bundle, version)).Select(s => s.Base64));
    }

    /// <summary>省略と明示を保持し、同日時の最小ルールだけを試算します。</summary>
    [Fact]
    public void PreservesRuleNumbersOmissionAndExclusiveEnd()
    {
        var (service, bundle) = Create();
        var draft = DefinitionEditor.Project(bundle, 4);
        var attributes = draft.Schedules![0].Attributes;
        attributes["sd"] = ["7,2024/2/29", "2,2024/2/29", "3,2024/3/1"];
        attributes["st"] = ["7,06:00", "2,+00:00", "3,06:00"];
        attributes["sh"] = ["7,no", "2,no", "3,no"];
        attributes["ed"] = ["2024/3/1"];
        draft.Calendars![0].Attributes.Remove("md");
        var compiled = service.Validate(DefinitionEditor.Render(bundle, draft));
        var root = compiled.Units.Flatten().Single(u => u.Path == "/g/n");
        Assert.Equal(attributes["sd"], root.Attributes["sd"]);
        Assert.False(compiled.Units[0].Attributes.ContainsKey("md"));
        var request = new EditorScheduleRequest(draft, "/g/n", new(2024, 2, 1), new(2024, 2, 29), new(2024, 3, 1));
        var point = Assert.Single(DefinitionEditor.PreviewSchedule(compiled, request, new()).Points);
        Assert.Equal(2, point.Rule);
        Assert.Equal(new DateOnly(2024, 2, 29), point.BusinessDate);
        Assert.Equal(DateTimeOffset.Parse("2024-02-28T21:00:00Z"), point.Due);
        attributes.Remove("sd");
        attributes.Remove("st");
        attributes.Remove("sh");
        attributes.Remove("ed");
        compiled = service.Validate(DefinitionEditor.Render(bundle, draft));
        Assert.False(compiled.Units.Flatten().Single(u => u.Path == "/g/n").Attributes.ContainsKey("sd"));
        Assert.Single(DefinitionEditor.PreviewSchedule(compiled, request with { Anchor = new(2024, 2, 29) }, new()).Points);
    }

    /// <summary>変更値の境界・構文注入と未対応のカレンダー指定を拒否します。</summary>
    /// <param name="calendar">カレンダー属性かどうかです。</param>
    /// <param name="key">変更する属性です。</param>
    /// <param name="value">拒否する値です。</param>
    [Theory]
    [InlineData(false, "sd", "1,2023/2/29")]
    [InlineData(false, "sd", "1,2026/13/1")]
    [InlineData(false, "sd", "1,0")]
    [InlineData(false, "sd", "1,+36")]
    [InlineData(false, "sd", "1,b-31")]
    [InlineData(false, "sd", "ud")]
    [InlineData(false, "st", "1,48:00")]
    [InlineData(false, "st", "1,09:00;ha=n")]
    [InlineData(false, "cy", "1,(13,m)")]
    [InlineData(false, "ln", "1,145")]
    [InlineData(false, "ed", "2026/2/30")]
    [InlineData(false, "jc", "/missing")]
    [InlineData(false, "de", "y")]
    [InlineData(true, "stt", "24:00")]
    [InlineData(true, "sdd", "29")]
    [InlineData(true, "sdd", "mo:5")]
    [InlineData(true, "md", "")]
    [InlineData(true, "cl", "2026/2/30")]
    public void RejectsInvalidChangedValues(bool calendar, string key, string value)
    {
        var (service, bundle) = Create();
        var draft = DefinitionEditor.Project(bundle, 4);
        (calendar ? draft.Calendars![0] : draft.Schedules![0]).Attributes[key] = [value];
        var error = Assert.ThrowsAny<Exception>(() => service.Validate(DefinitionEditor.Render(bundle, draft)));
        Assert.True(error is FormatException or ArgumentException, error.ToString());
    }

    /// <summary>参照カレンダーを編集し、48時間制の実日時と業務日を分離します。</summary>
    [Fact]
    public void UsesEditedReferenceCalendarAndTimeMode()
    {
        var (service, bundle) = Create();
        var draft = DefinitionEditor.Project(bundle, 4);
        draft.Schedules![0].Attributes["jc"] = ["/cal"];
        draft.Schedules[0].Attributes["st"] = ["1,+47:59"];
        draft.Calendars![1].Attributes["stt"] = ["06:00"];
        var compiled = service.Validate(DefinitionEditor.Render(bundle, draft));
        var request = new EditorScheduleRequest(draft, "/g/n", new(2026, 11, 6), new(2026, 11, 6), new(2026, 11, 7));
        var point = Assert.Single(DefinitionEditor.PreviewSchedule(compiled, request, new()).Points);
        Assert.Equal(DateTimeOffset.Parse("2026-11-07T20:59:00Z"), point.Due);
        Assert.Throws<FormatException>(() => new DefinitionService(new StorageRepository(new()), new() { ScheduleTimeMode = 24 }).Validate(DefinitionEditor.Render(bundle, draft)));
    }

    /// <summary>上位のないネスト計算、範囲超過、欠落投影やグループ改変を拒否します。</summary>
    [Fact]
    public void RejectsIncompleteProjectionAndUnsupportedTrial()
    {
        var (service, bundle) = Create();
        var draft = DefinitionEditor.Project(bundle, 4);
        Assert.Throws<ArgumentException>(() => DefinitionEditor.Render(bundle, draft with { Schedules = null }));
        Assert.Throws<ArgumentException>(() => DefinitionEditor.Render(bundle, draft with { Calendars = [] }));
        var compiled = service.Validate(DefinitionEditor.Render(bundle, draft));
        var request = new EditorScheduleRequest(draft, "/g/n/child", new(2026, 1, 1), new(2026, 1, 1), new(2026, 2, 1));
        Assert.Throws<ArgumentException>(() => DefinitionEditor.PreviewSchedule(compiled, request, new()));
        Assert.Throws<ArgumentException>(() => DefinitionEditor.PreviewSchedule(compiled, request with { NetworkId = "/g/n", Until = new(2028, 1, 1) }, new()));
        Assert.Throws<ArgumentException>(() => DefinitionEditor.PreviewSchedule(compiled, request with { NetworkId = "/g/n", Until = request.From }, new()));
        draft.Schedules![0].Attributes["sd"] = ["1,en", "1,2026/1/1"];
        Assert.Throws<FormatException>(() => service.Validate(DefinitionEditor.Render(bundle, draft)));
        draft.Schedules[0].Attributes["sd"] = ["0,ud", "1,en"];
        Assert.Throws<ArgumentException>(() => DefinitionEditor.Render(bundle, draft));
    }

    /// <summary>夏時間の欠落と重複を推測で解決しません。</summary>
    /// <param name="date">切替日です。</param>
    /// <param name="time">曖昧または存在しない時刻です。</param>
    [Theory]
    [InlineData("2026/3/8", "02:30")]
    [InlineData("2026/11/1", "01:30")]
    public void RejectsDaylightSavingAmbiguity(string date, string time)
    {
        var (service, bundle) = Create();
        var draft = DefinitionEditor.Project(bundle, 4);
        draft.Schedules![0].Attributes["sd"] = [date];
        draft.Schedules[0].Attributes["st"] = [time];
        draft.Calendars![0].Attributes.Remove("stt");
        var compiled = service.Validate(DefinitionEditor.Render(bundle, draft));
        var day = DateOnly.ParseExact(date, "yyyy/M/d");
        Assert.Throws<FormatException>(() => DefinitionEditor.PreviewSchedule(compiled, new(draft, "/g/n", day, day, day.AddDays(1)), new() { TimeZone = "America/New_York" }));
    }

    /// <summary>配置で補われたグループは原文がないので編集対象に出しません。</summary>
    [Fact]
    public void ExcludesSyntheticGroupsFromEditableCalendars()
    {
        var (service, bundle) = Create();
        bundle.Sources[0].Mapping.TargetPath = "/external/team";
        bundle = service.Validate(bundle.Sources);
        var draft = DefinitionEditor.Project(bundle, 4);
        Assert.DoesNotContain(draft.Calendars!, c => c.Id is "/external" or "/external/team");
        Assert.Equal(bundle.Sources.Select(s => s.Base64), DefinitionEditor.Render(bundle, draft).Select(s => s.Base64));
        draft.Calendars!.Single(c => c.Id == "/external/team/g").Attributes["md"] = ["th"];
        Assert.Contains(service.Validate(DefinitionEditor.Render(bundle, draft)).Units.Flatten(), u => u.Path == "/external/team/g" && u.Get("md") == "th");
    }

    /// <summary>画面の候補上限を超える場合は部分結果を実行予定のように返しません。</summary>
    [Fact]
    public void RejectsMoreThanTwoThousandTrialPoints()
    {
        var (service, bundle) = Create();
        var draft = DefinitionEditor.Project(bundle, 4);
        var attributes = draft.Schedules![0].Attributes;
        attributes["sd"] = Enumerable.Range(1, 8).Select(i => $"{i},en").ToList();
        attributes["st"] = Enumerable.Range(1, 8).Select(i => $"{i},{i + 6:D2}:00").ToList();
        attributes["cy"] = Enumerable.Range(1, 8).Select(i => $"{i},(1,d)").ToList();
        attributes["sh"] = Enumerable.Range(1, 8).Select(i => $"{i},no").ToList();
        var compiled = service.Validate(DefinitionEditor.Render(bundle, draft));
        Assert.Throws<ArgumentException>(() => DefinitionEditor.PreviewSchedule(compiled, new(draft, "/g/n", new(2026, 1, 1), new(2026, 1, 1), new(2027, 1, 1)), new()));
    }

    private static (DefinitionService Service, DefinitionBundle Bundle) Create(string encoding = "utf-8", bool bom = false)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var options = new RunnerOptions { ScheduleTimeMode = 48 };
        var service = new DefinitionService(new StorageRepository(options), options);
        var sources = new List<SourceEvidence>
        {
            new() { Mapping = new() { BlobPath = "input/a-main.ajs", Encoding = encoding }, Base64 = Convert.ToBase64String(Encoding.GetEncoding(encoding).GetBytes((bom ? "\uFEFF" : string.Empty) + Text)) },
            new() { Mapping = new() { BlobPath = "input/z-cal.ajs" }, Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("unit=cal;{ty=g;op=mo;cl=su;}")) },
        };
        return (service, service.Validate(sources));
    }
}
