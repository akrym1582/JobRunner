using System.Text;
using System.Text.Json;
using JobRunner.Controllers.Ui;
using JobRunner.Editing;
using JobRunner.Models;
using JobRunner.Services;
using JobRunner.Storage;
using Xunit;

namespace JobRunner.Tests;

/// <summary>同じ入力Blobの別マッピングを、原文比較で混同しないことを検証します。</summary>
public sealed class DefinitionEditorSourceIdentityTests
{
    /// <summary>同じ実Blobを異なる配置先・文字コードで読み、片方だけ編集して原文と所属を保ちます。</summary>
    /// <returns>実入力、解析、投影、原文比較の対応を確認します。</returns>
    [Fact]
    public async Task SharedBlobMappingsRetainTheirIdentityThroughEditing()
    {
        await using var scope = new StorageScope();
        await scope.Repository.InitializeAsync(CancellationToken.None);
        var input = scope.Repository.Blobs.GetBlobContainerClient("mapping-" + Guid.NewGuid().ToString("N"));
        await input.CreateAsync();
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes("/* café */\nunit=net;{ty=n;sd=0,ud;el=job,j,+80 +48;unit=job;{ty=j;te=\"echo original\";}}");
            await input.GetBlobClient("shared.ajs").UploadAsync(new BinaryData(bytes));
            string path = input.Name + "/shared.ajs";
            scope.Options.DefinitionSources =
            [
                new() { BlobPath = path, TargetPath = "/alpha", Encoding = "utf-8" },
                new() { BlobPath = path, TargetPath = "/beta", Encoding = "shift_jis" },
            ];
            var service = new DefinitionService(scope.Repository, scope.Options);
            var bundle = await service.LoadSourcesAsync(CancellationToken.None);
            var original = DefinitionEditor.Describe(bundle.Sources, bundle.Sources);
            Assert.Equal(2, original.Count);
            Assert.All(original, source =>
            {
                Assert.Equal(path, source.FileName);
                Assert.Equal("Unit", source.Kind);
                Assert.False(source.Changed);
            });
            Assert.Equal(["/alpha", "/beta"], original.Select(s => s.TargetPath));
            Assert.Equal(["utf-8", "shift_jis"], original.Select(s => s.Encoding));
            Assert.NotEqual(original[0].Text, original[1].Text);
            var draft = DefinitionEditor.Project(bundle, 9);
            draft.Networks.Single(n => n.Path == "/beta/net").Nodes[0].Attributes["te"] = ["echo beta"];
            var rendered = DefinitionEditor.Render(bundle, draft);
            Assert.Equal(bytes, Convert.FromBase64String(rendered[0].Base64));
            var described = DefinitionEditor.Describe(rendered, bundle.Sources);
            Assert.False(described[0].Changed);
            Assert.True(described[1].Changed);
            Assert.Equal("/beta", described[1].TargetPath);
            Assert.Equal("shift_jis", described[1].Encoding);
            Assert.Contains("echo beta", described[1].Text, StringComparison.Ordinal);
            Assert.Equal(2, service.Validate(rendered).Sources.Count);
        }
        finally
        {
            await input.DeleteIfExistsAsync();
        }
    }

    /// <summary>同じバイト列でも、4項目のいずれかが違えば別の原文マッピングとして比較します。</summary>
    /// <param name="field">変更する識別項目です。</param>
    [Theory]
    [InlineData("Kind")]
    [InlineData("TargetPath")]
    [InlineData("BlobPath")]
    [InlineData("Encoding")]
    public void DescriptionDistinguishesEachMappingField(string field)
    {
        var original = Source();
        var changed = Source();
        switch (field)
        {
            case "Kind": changed.Mapping.Kind = "Calendar"; break;
            case "TargetPath": changed.Mapping.TargetPath = "/other"; break;
            case "BlobPath": changed.Mapping.BlobPath = "input/other.ajs"; break;
            case "Encoding": changed.Mapping.Encoding = "us-ascii"; break;
        }

        var described = Assert.Single(DefinitionEditor.Describe([changed], [original]));
        Assert.True(described.Changed);
        Assert.Equal(changed.Mapping.Kind, described.Kind);
    }

    /// <summary>入力識別子は文字コード違いだけの重複を許さず、未対応の種別も新たに許可しません。</summary>
    [Fact]
    public void InputValidationKeepsDuplicateAndUnsupportedKindRules()
    {
        var options = new RunnerOptions();
        var service = new DefinitionService(new StorageRepository(options), options);
        var first = Source();
        var second = Source();
        second.Mapping.Encoding = "us-ascii";
        options.DefinitionSources = [first.Mapping, second.Mapping];
        Assert.Throws<FormatException>(() => service.ValidateOptions());
        Assert.Throws<FormatException>(() => service.Validate([first, second]));
        second.Mapping.Kind = "Calendar";
        Assert.Throws<FormatException>(() => service.Validate([second]));
        options.DefinitionSources = [second.Mapping];
        Assert.Throws<FormatException>(() => service.ValidateOptions());
    }

    /// <summary>種別を持たない旧ブラウザー/共有下書きの基点はUnitとして復元します。</summary>
    [Fact]
    public void LegacySourceWithoutKindDefaultsToUnit()
    {
        var value = JsonSerializer.Deserialize<EditorSource>("{\"fileName\":\"input/jobs\",\"encoding\":\"utf-8\",\"targetPath\":\"/\",\"text\":\"original\",\"changed\":false}", new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("Unit", value!.Kind);
    }

    private static SourceEvidence Source() => new() { Mapping = new() { BlobPath = "input/shared.ajs", TargetPath = "/base" }, Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("unit=net;{ty=n;sd=0,ud;}")) };
}
