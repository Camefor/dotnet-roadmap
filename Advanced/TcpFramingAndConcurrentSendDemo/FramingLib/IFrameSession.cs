namespace FramingLib;

/// <summary>
/// 一个连接的发送通道。两种实现用于对照：
/// <see cref="UnsafeSession"/> 直接并发写 socket，<see cref="QueuedSession"/> 用队列串行化
/// </summary>
public interface IFrameSession : IDisposable
{
    bool Send(byte[] frame);

    /// <summary>
    /// 等待已提交的数据真正写出。
    /// 异步发送队列必须有优雅关闭，否则关连接时队列里的尾部数据会被直接丢掉
    /// </summary>
    void Flush();

    /// <summary>发送队列达到过的最大积压字节数，用于观察背压</summary>
    long PeakPendingBytes { get; }

    /// <summary>当前仍在队列里等待写出的字节数；同步写入的实现恒为 0</summary>
    long PendingBytes { get; }
}
