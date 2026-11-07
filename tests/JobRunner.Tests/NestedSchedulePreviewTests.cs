using System.Text;
using JobRunner.Controllers.Ui;
using JobRunner.Editing;
using JobRunner.Models;
using JobRunner.Services;
using JobRunner.Storage;
using Xunit;

namespace JobRunner.Tests;

/// <summary>V12の親子リンク、同一業務日、最早候補を独立した期待値で確認します。</summary>
public sealed class NestedSchedulePreviewTests
{
    /// <summary>親予定なし・独自ルール・孫への対象外伝播を区別します。</summary>
    [Fact]
    public void ExplainsMissingRootAndAncestorExclusion()
    {
        var (service, bundle, draft) = Compile("unit=n;{ty=n;sd=fr;cy=(1,w);st=10:00;sh=no;unit=c;{ty=n;de=n;sd=2026/11/6;st=11:00;ln=1,1;sh=no;unit=g;{ty=n;de=n;sd=2026/11/6;ln=1,2;sh=no;unit=h;{ty=n;}}}}");
        var request = new EditorScheduleRequest(draft, "/n/c", new(2026, 11, 1), new(2026, 11, 5), new(2026, 11, 8));
        var result = DefinitionEditor.PreviewNestedSchedule(bundle, request, new() { TimeZone = "UTC" });
        Assert.Equal(new[] { "ROOT_NOT_SCHEDULED", "SELECTED", "ROOT_NOT_SCHEDULED" }, result.Rows.Select(r => r.Reason));
        Assert.Equal(new DateTimeOffset(2026, 11, 6, 11, 0, 0, TimeSpan.Zero), result.Rows[1].Selected!.Due);
        result = DefinitionEditor.PreviewNestedSchedule(bundle, request with { NetworkId = "/n/c/g/h" }, new());
        Assert.Equal("ANCESTOR_EXCLUDED", result.Rows[1].Reason);
        Assert.Equal("RULE_NOT_LINKED", result.Rows[1].Chain[2].Reason);
        Assert.Equal(4, result.Rows[1].Chain.Count);
        Assert.False(result.Rows[1].Eligible);
        Assert.Null(result.Rows[1].Selected);
        Assert.Equal(bundle.Sources[0].Base64, Assert.Single(DefinitionEditor.Render(bundle, draft)).Base64);
        _ = service;
    }

    /// <summary>同日時の無効ルールより、基準時刻を満たす有効ルールを先に採用します。</summary>
    [Fact]
    public void FiltersBusinessTimeBeforeChoosingSameDueRule()
    {
        var (_, bundle, draft) = Compile("unit=g;{ty=g;stt=06:00;unit=n;{ty=n;sd=2026/11/5;st=10:00;sh=no;unit=c;{ty=n;de=n;sd=1,2026/11/5;sd=2,2026/11/5;st=1,05:00;st=2,+23:00;sh=1,no;sh=2,no;ln=1,1;ln=2,1;}}}");
        var result = DefinitionEditor.PreviewNestedSchedule(bundle, new(draft, "/g/n/c", new(2026, 11, 5), new(2026, 11, 5), new(2026, 11, 6)), new() { TimeZone = "UTC" });
        var row = Assert.Single(result.Rows);
        Assert.True(row.Eligible);
        Assert.Equal(2, row.Selected!.Rule);
        Assert.Equal(new DateOnly(2026, 11, 5), row.Selected.BusinessDate);
        Assert.Equal(new DateTimeOffset(2026, 11, 6, 5, 0, 0, TimeSpan.Zero), row.Selected.Due);
    }

    /// <summary>上位の別世代を同じ子日時でも別行として返します。</summary>
    [Fact]
    public void KeepsSeparateRootContextsAndChoosesEarliestChild()
    {
        var (_, bundle, draft) = Compile("unit=n;{ty=n;sd=1,2026/11/5;sd=2,2026/11/5;st=1,09:00;st=2,10:00;sh=1,no;sh=2,no;unit=c;{ty=n;de=n;sd=1,2026/11/5;sd=2,2026/11/5;sd=3,2026/11/5;sd=4,2026/11/5;st=1,12:00;st=2,11:00;st=3,11:00;st=4,11:00;ln=1,1;ln=2,1;ln=3,1;ln=4,2;sh=1,no;sh=2,no;sh=3,no;sh=4,no;}}");
        var result = DefinitionEditor.PreviewNestedSchedule(bundle, new(draft, "/n/c", new(2026, 11, 5), new(2026, 11, 5), new(2026, 11, 6)), new() { TimeZone = "UTC" });
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(new[] { 1, 2 }, result.Rows.Select(r => r.Root!.Rule));
        Assert.Equal(new[] { 2, 4 }, result.Rows.Select(r => r.Selected!.Rule));
        Assert.Single(result.Rows.Select(r => r.Selected!.Due).Distinct());
    }

    /// <summary>親の同時刻重複で無効となったルールにリンクした子は対象外です。</summary>
    [Fact]
    public void UsesOnlyWinningRootRule()
    {
        var (_, bundle, draft) = Compile("unit=n;{ty=n;sd=1,2026/11/5;sd=2,2026/11/5;st=1,10:00;st=2,10:00;sh=1,no;sh=2,no;unit=c;{ty=n;de=n;sd=2026/11/5;ln=1,2;sh=no;}}");
        var row = Assert.Single(DefinitionEditor.PreviewNestedSchedule(bundle, new(draft, "/n/c", new(2026, 11, 5), new(2026, 11, 5), new(2026, 11, 6)), new()).Rows);
        Assert.Equal(1, row.Root!.Rule);
        Assert.Equal("RULE_NOT_LINKED", row.Reason);
    }

    /// <summary>子の起算カレンダーを評価し、親が毎日でも指定運用日だけを採用します。</summary>
    [Fact]
    public void AppliesReferencedCalendarAndMonthlyOperatingOffset()
    {
        var (_, bundle, draft) = Compile("unit=cal;{ty=g;cl=sa;cl=su;cl=2026/11/23;}unit=n;{ty=n;sd=en;cy=(1,d);st=10:00;sh=no;unit=c;{ty=n;de=n;jc=/cal;sd=25;cy=(1,m);cftd=be,5,10;st=11:00;sh=no;ln=1,1;}}");
        var rows = DefinitionEditor.PreviewNestedSchedule(bundle, new(draft, "/n/c", new(2026, 11, 1), new(2026, 11, 16), new(2026, 11, 19)), new()).Rows;
        Assert.Equal(new[] { false, true, false }, rows.Select(r => r.Eligible));
        Assert.Equal(new DateOnly(2026, 11, 17), rows[1].Selected!.BusinessDate);
    }

    /// <summary>不適合時刻と同日候補なしの理由を分離します。</summary>
    [Fact]
    public void ExplainsDayAndTimeMismatch()
    {
        var (_, bundle, draft) = Compile("unit=g;{ty=g;stt=06:00;unit=n;{ty=n;sd=2026/11/5;st=10:00;sh=no;unit=c;{ty=n;de=n;sd=2026/11/5;st=05:00;sh=no;}}}");
        var request = new EditorScheduleRequest(draft, "/g/n/c", new(2026, 11, 5), new(2026, 11, 5), new(2026, 11, 6));
        Assert.Equal("BUSINESS_TIME_MISMATCH", Assert.Single(DefinitionEditor.PreviewNestedSchedule(bundle, request, new()).Rows).Reason);
        var entries = draft.Schedules!.Select(s => s.Id == "/g/n/c" ? s with { Attributes = new(s.Attributes) { ["sd"] = ["2026/11/6"] } } : s).ToList();
        draft = draft with { Schedules = entries };
        var service = new DefinitionService(new StorageRepository(new()), new());
        bundle = service.Validate(DefinitionEditor.Render(bundle, draft));
        Assert.Equal("NO_SAME_BUSINESS_DATE", Assert.Single(DefinitionEditor.PreviewNestedSchedule(bundle, request with { Draft = draft }, new()).Rows).Reason);
    }

    /// <summary>親依存の孫は、採用した親ルールをそのまま使います。</summary>
    [Fact]
    public void InheritsSelectedParentRuleThroughRenamedRoot()
    {
        var (service, bundle, draft) = Compile("unit=n;{ty=n;sd=2026/11/5;st=10:00;sh=no;unit=c;{ty=n;de=n;sd=7,2026/11/5;st=7,11:00;ln=7,1;sh=7,no;unit=h;{ty=n;}}}");
        var root = draft.Roots![0];
        draft = draft with { Roots = [root with { Node = root.Node with { Name = "renamed" } }] };
        var compiled = service.Validate(DefinitionEditor.Render(bundle, draft));
        var result = DefinitionEditor.PreviewNestedSchedule(compiled, new(draft, "/n/c/h", new(2026, 11, 5), new(2026, 11, 5), new(2026, 11, 6)), new());
        Assert.Equal("/n", result.RootNetworkId);
        Assert.Equal("/renamed", result.RootPath);
        var row = Assert.Single(result.Rows);
        Assert.Equal("INHERITED", row.Reason);
        Assert.Equal(7, row.Selected!.Rule);
        Assert.Equal("/renamed/c/h", row.Chain[^1].Path);
    }

    /// <summary>グループ移動で再投影したIDと原文を使い、祖先経路を旧パスと混同しません。</summary>
    [Fact]
    public void ResolvesNestedScheduleAfterGroupMove()
    {
        var (service, bundle, draft) = Compile("unit=g;{ty=g;unit=n;{ty=n;sd=2026/11/5;st=10:00;sh=no;unit=c;{ty=n;de=n;sd=2026/11/5;st=11:00;sh=no;ln=1,1;unit=h;{ty=n;}}}}unit=target;{ty=g;}");
        var moved = DefinitionEditor.Move(bundle, new(draft, "/g", "/target", 0), service, new());
        var compiled = service.Validate(DefinitionEditor.Render(bundle, moved.Draft));
        var result = DefinitionEditor.PreviewNestedSchedule(compiled, new(moved.Draft, "/target/g/n/c/h", new(2026, 11, 5), new(2026, 11, 5), new(2026, 11, 6)), new());
        Assert.Equal("/target/g/n", result.RootPath);
        var row = Assert.Single(result.Rows);
        Assert.True(row.Eligible);
        Assert.Equal(new[] { "/target/g/n", "/target/g/n/c", "/target/g/n/c/h" }, row.Chain.Select(step => step.Path));
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 2, 0, 0, TimeSpan.Zero), row.Selected!.Due);
    }

    /// <summary>取消・種別・範囲と、ルート予定なしを含む全行数の上限を検証します。</summary>
    [Fact]
    public void ValidatesTargetRangeCancellationAndTotalRows()
    {
        var schedule = string.Concat(Enumerable.Range(1, 10).Select(i => $"sd={i},en;st={i},00:{i:00};cy={i},(2,d);sh={i},no;"));
        var (_, bundle, draft) = Compile("unit=n;{ty=n;" + schedule + "unit=c;{ty=n;}}");
        var request = new EditorScheduleRequest(draft, "/n/c", new(2026, 1, 1), new(2026, 1, 1), new(2027, 1, 1));
        Assert.Throws<ArgumentException>(() => DefinitionEditor.PreviewNestedSchedule(bundle, request, new()));
        Assert.Throws<ArgumentException>(() => DefinitionEditor.PreviewNestedSchedule(bundle, request with { NetworkId = "/n" }, new()));
        Assert.Throws<ArgumentException>(() => DefinitionEditor.PreviewNestedSchedule(bundle, request with { Until = new(2028, 1, 1) }, new()));
        Assert.Throws<OperationCanceledException>(() => DefinitionEditor.PreviewNestedSchedule(bundle, request, new(), new CancellationToken(true)));
    }

    /// <summary>Storageへアクセスせず、原文を検証して編集投影を作ります。</summary>
    /// <param name="text">試験用の標準定義です。</param>
    /// <returns>検証サービス・定義・下書きです。</returns>
    private static (DefinitionService Service, DefinitionBundle Bundle, EditorDraft Draft) Compile(string text)
    {
        var service = new DefinitionService(new StorageRepository(new()), new());
        var bundle = service.Validate([new SourceEvidence { Mapping = new() { BlobPath = "nested.ajs" }, Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) }]);
        return (service, bundle, DefinitionEditor.Project(bundle, 9));
    }
}
