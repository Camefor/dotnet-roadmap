using System.Net.Sockets;

namespace FramingLib;

/// <summary>
/// 反面教材：多个线程直接并发写同一个 Socket，没有任何串行化。
///
/// 这样的代码在小包场景下可以稳定运行很多年——小包一次就能塞进发送缓冲区，
/// 单次写入是原子的，看起来毫无问题。
/// 但只要有一帧大到必须分多次写出（缓冲区装不下），窗口就打开了：
/// 本线程写完第一块、还没写第二块时，另一个线程的块插了进来，
/// 两帧的字节在 TCP 流里交织，接收端按长度字段切出来的内容随之错乱。
///
/// 真实环境里的分块由内核在缓冲区满时自动产生，
/// 这里通过 <see cref="SocketWriter.WriteChunked"/> 显式分块来稳定复现。
/// </summary>
public sealed class UnsafeSession : IFrameSession
{
    private readonly Socket _socket;
    private readonly int _chunkSize;

    public UnsafeSession(Socket socket, int chunkSize)
    {
        _socket = socket;
        _chunkSize = chunkSize;
    }

    public long PeakPendingBytes => 0;

    //同步写入，调用返回时字节已交给内核，不存在应用层积压
    public long PendingBytes => 0;

    public bool Send(byte[] frame)
    {
        try
        {
            //多个线程同时进到这里，各自的块就会互相插队
            SocketWriter.WriteChunked(_socket, frame, _chunkSize);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public void Flush()
    {
        //同步写入，调用返回即已提交给内核，无需额外等待
    }

    public void Dispose()
    {
    }
}
