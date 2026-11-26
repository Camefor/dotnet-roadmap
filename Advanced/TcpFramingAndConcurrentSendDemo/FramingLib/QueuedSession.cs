using System.Collections.Concurrent;
using System.Net.Sockets;

namespace FramingLib;

/// <summary>
/// 正确做法：每个连接一条发送队列 + 一个专属发送线程。
///
/// 解决三个问题：
///   1. 串行化——实际的 Socket.Send 只由发送线程调用，物理上杜绝交织
///   2. 解耦——业务线程只负责入队，不会被慢消费者阻塞
///   3. 背压——积压超过上限说明对端消费不过来，此时队列里多半是过期数据，
///      继续堆积只会 OOM，不如主动断开让对端重连
///
/// 注意不要用"加锁"代替队列：加锁同样能串行化，但业务线程会排在大帧后面，
/// 一个慢消费者就能拖住所有推送线程。
/// </summary>
public sealed class QueuedSession : IFrameSession
{
    private readonly Socket _socket;
    private readonly long _maxPendingBytes;
    private readonly int _chunkSize;
    private readonly BlockingCollection<byte[]> _queue = new(new ConcurrentQueue<byte[]>());
    private readonly Thread _sendThread;

    private long _pendingBytes;
    private long _peakPendingBytes;
    private volatile bool _faulted;

    public QueuedSession(Socket socket, long maxPendingBytes, int chunkSize)
    {
        _socket = socket;
        _maxPendingBytes = maxPendingBytes;
        _chunkSize = chunkSize;

        _sendThread = new Thread(SendLoop)
        {
            Name = "SessionSend",
            IsBackground = true
        };
        _sendThread.Start();
    }

    public long PeakPendingBytes => Interlocked.Read(ref _peakPendingBytes);

    public long PendingBytes => Interlocked.Read(ref _pendingBytes);

    /// <summary>发送通道是否已损坏，损坏后连接不再可用</summary>
    public bool Faulted => _faulted;

    /// <summary>因背压被拒绝的帧数</summary>
    public int RejectedFrameCount { get; private set; }

    public bool Send(byte[] frame)
    {
        if (_faulted || _queue.IsAddingCompleted)
        {
            return false;
        }

        var pending = Interlocked.Add(ref _pendingBytes, frame.Length);
        UpdatePeak(pending);

        if (pending > _maxPendingBytes)
        {
            Interlocked.Add(ref _pendingBytes, -frame.Length);
            RejectedFrameCount++;
            Fault();
            return false;
        }

        try
        {
            _queue.Add(frame);
            return true;
        }
        catch (InvalidOperationException)
        {
            //队列已关闭（ObjectDisposedException 也继承自它）
            Interlocked.Add(ref _pendingBytes, -frame.Length);
            return false;
        }
    }

    public void Flush()
    {
        _queue.CompleteAdding();
        _sendThread.Join();
    }

    public void Dispose()
    {
        if (!_queue.IsAddingCompleted)
        {
            _queue.CompleteAdding();
        }
        _queue.Dispose();
    }

    private void SendLoop()
    {
        try
        {
            foreach (var frame in _queue.GetConsumingEnumerable())
            {
                Interlocked.Add(ref _pendingBytes, -frame.Length);
                try
                {
                    //同样是分块写出，但只有这一个线程在写，块之间不会被插队
                    SocketWriter.WriteChunked(_socket, frame, _chunkSize);
                }
                catch (SocketException)
                {
                    //写失败后连接状态已不确定，可能只发出了半帧，
                    //继续发会让对端一直错帧，不如直接判定这个连接报废
                    Fault();
                    return;
                }
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Fault()
    {
        _faulted = true;
    }

    private void UpdatePeak(long pending)
    {
        long peak;
        do
        {
            peak = Interlocked.Read(ref _peakPendingBytes);
            if (pending <= peak)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _peakPendingBytes, pending, peak) != peak);
    }
}
