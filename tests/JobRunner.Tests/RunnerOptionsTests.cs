using JobRunner.Import;
using JobRunner.Models;
using JobRunner.Scheduling;
using JobRunner.Storage;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace JobRunner.Tests;

/// <summary>環境変数と同じ構成バインドで起動時の既定値を確認します。</summary>
public sealed class RunnerOptionsTests
{
    /// <summary>未指定と空の構成値でBlob・Tableの両方が開発用接続先を使います。</summary>
    /// <param name="value">接続文字列の構成値です。</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    public void EmptyConnectionUsesDevelopmentStorage(string? value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["JobRunner:ConnectionString"] = value }).Build();
        var options = configuration.GetSection("JobRunner").Get<RunnerOptions>() ?? new();
        Assert.Equal("UseDevelopmentStorage=true", options.ConnectionString);
        Assert.Equal(48, options.ScheduleTimeMode);
        var storage = new StorageRepository(options);
        Assert.Equal(10000, storage.Blobs.Uri.Port);
        Assert.Equal(10002, storage.Table.Uri.Port);
        options.ConnectionString = value;
        Assert.Equal("UseDevelopmentStorage=true", options.ConnectionString);
    }

    /// <summary>明示接続先と24時間制は、既定値へ置換しません。</summary>
    [Fact]
    public void KeepsExplicitSettings()
    {
        const string connection = "UseDevelopmentStorage=true;DevelopmentStorageProxyUri=http://localhost";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["JobRunner:ConnectionString"] = connection, ["JobRunner:ScheduleTimeMode"] = "24" }).Build();
        var options = configuration.GetSection("JobRunner").Get<RunnerOptions>()!;
        Assert.Equal(connection, options.ConnectionString);
        Assert.Equal(24, options.ScheduleTimeMode);
        options.ConnectionString = string.Empty;
        Assert.Equal("UseDevelopmentStorage=true", options.ConnectionString);
    }

    /// <summary>既定設定で25時の予定を翌日実日時へ変換し、元の業務日を保ちます。</summary>
    [Fact]
    public void Default48HoursPreservesBusinessDate()
    {
        var options = new RunnerOptions();
        var units = Jp1DefinitionParser.Parse("unit=n;{ty=n;sd=2026/11/5;st=25:00;sh=no;}", new DefinitionSource());
        DefinitionValidator.Validate(units, options);
        var day = new DateOnly(2026, 11, 5);
        var point = Assert.Single(ScheduleCalculator.Calculate(units[0], units.ToDictionary(u => u.Path), new Registration { Anchor = day }, day, day.AddDays(1), options));
        Assert.Equal(day, point.BusinessDate);
        Assert.Equal(DateTimeOffset.Parse("2026-11-05T16:00:00Z", System.Globalization.CultureInfo.InvariantCulture), point.Due);
    }

    /// <summary>配布設定も空の上書きと48時間制の既定で起動できます。</summary>
    [Fact]
    public void AppSettingsDefaultMatchesModel()
    {
        string directory = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(directory, "JobRunner.slnx")))
        {
            directory = Directory.GetParent(directory)!.FullName;
        }

        var configuration = new ConfigurationBuilder().AddJsonFile(Path.Combine(directory, "src/JobRunner/appsettings.json")).AddInMemoryCollection(new Dictionary<string, string?> { ["JobRunner:ConnectionString"] = string.Empty }).Build();
        var options = configuration.GetSection("JobRunner").Get<RunnerOptions>()!;
        Assert.Equal(48, options.ScheduleTimeMode);
        Assert.Equal(new RunnerOptions().ScheduleTimeMode, options.ScheduleTimeMode);
        Assert.Equal("UseDevelopmentStorage=true", options.ConnectionString);
    }
}
