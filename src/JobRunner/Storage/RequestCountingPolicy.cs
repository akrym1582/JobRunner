using Azure.Core;
using Azure.Core.Pipeline;

namespace JobRunner.Storage;

/// <summary>SDKが実際に送信した要求数を数え、Storage負荷の測定と運用診断に使用します。</summary>
public sealed class RequestCountingPolicy : HttpPipelineSynchronousPolicy
{
    /// <summary>BlobとTableの非同期送信が共有する単調増加カウンターです。</summary>
    private long count;

    /// <summary>Gets. 再試行を含む送信回数です。通信前に拒否した処理は含めません。</summary>
    public long Count => Interlocked.Read(ref count);

    /// <summary>実際の送信直前に回数を加算し、API種類に依存せず測定します。</summary>
    /// <param name="message">送信する要求です。接続情報や本文は記録しません。</param>
    public override void OnSendingRequest(HttpMessage message) => Interlocked.Increment(ref count);
}
