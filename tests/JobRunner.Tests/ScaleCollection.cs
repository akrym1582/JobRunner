using Xunit;

namespace JobRunner.Tests;

/// <summary>他のテストによるプロセス・メモリ負荷を混ぜない測定用コレクションです。</summary>
[CollectionDefinition("Scale", DisableParallelization = true)]
public sealed class ScaleCollection
{
}
