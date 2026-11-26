using System.Net.Sockets;

namespace FramingLib;

/// <summary>
/// 按块把一帧写出去，用来还原"一次 Send 被拆成多次写入"的过程。
///
/// 真实环境中这个拆分是内核自动完成的：
/// 当帧大于发送缓冲区剩余空间时，Linux 的 send() 会进入等待，
/// 而等待期间会释放 socket 锁（sk_stream_wait_memory），
/// 于是另一个线程的 send() 能拿到锁、把自己的字节追加进去——交织就此发生。
///
/// 这里显式分块，是为了让现象在任何平台上都能确定性复现。
/// 否则 Windows 的 Winsock 会对同一 socket 的并发 send 做串行化，
/// 同样一份有缺陷的代码在 Windows 上反而看不出问题，
/// 这也正是这类缺陷容易在本地测不出、上了 Linux 才爆发的原因。
///
/// 另外注意这里的 while 循环：Send 的返回值是"实际写出的字节数"，
/// 不处理部分写入的代码（例如直接 <c>socket.Send(frame) > 0</c> 就当成功）
/// 在非阻塞或超时场景下会静默丢掉剩余字节。
/// </summary>
public static class SocketWriter
{
    public static void WriteChunked(Socket socket, byte[] frame, int chunkSize)
    {
        var offset = 0;
        while (offset < frame.Length)
        {
            var size = Math.Min(chunkSize, frame.Length - offset);
            var written = socket.Send(frame, offset, size, SocketFlags.None);
            if (written <= 0)
            {
                throw new SocketException((int)SocketError.ConnectionReset);
            }
            offset += written;
        }
    }
}
