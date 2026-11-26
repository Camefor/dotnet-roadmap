using System.Net;
using System.Net.Sockets;

namespace StickyPacketDemo;

/// <summary>一次 Receive 的观测记录，用来把粘包、半包现象直接显示出来</summary>
public sealed record ReadTrace(int Index, int ReadBytes, int MessagesParsed, int PendingBytes);

public sealed record TransferResult(
    IReadOnlyList<byte[]> Received,
    long BytesSent,
    IReadOnlyList<ReadTrace> Trace);

/// <summary>
/// 在本机回环上真实跑一遍 TCP 传输，并记录每一次 Receive 的情况。
///
/// 接收端会先停顿一下再开始读，让发送端的数据在内核缓冲区里堆积起来——
/// 这样一次读取就能拿到好几条消息，粘包现象会稳定出现。
/// </summary>
public static class LoopbackTransfer
{
    public static TransferResult Run(
        IFramingStrategy strategy,
        IReadOnlyList<byte[]> messages,
        int readBufferSize,
        int startDelayMs)
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var port = ((IPEndPoint)listener.LocalEndPoint!).Port;

        long bytesSent = 0;
        Socket? server = null;

        var senderThread = new Thread(() =>
        {
            server = listener.Accept();
            foreach (var message in messages)
            {
                var frame = strategy.Encode(message);
                server.Send(frame);
                Interlocked.Add(ref bytesSent, frame.Length);
            }

            //发 FIN，接收端读到 0 字节即可干净收尾
            server.Shutdown(SocketShutdown.Send);
        })
        {
            Name = "StickySender",
            IsBackground = true
        };
        senderThread.Start();

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(IPAddress.Loopback, port);

        //先不读，让数据堆起来，制造"一次读到多条消息"
        Thread.Sleep(startDelayMs);

        var received = new List<byte[]>();
        var trace = new List<ReadTrace>();
        var buffer = new byte[readBufferSize];

        while (true)
        {
            var read = client.Receive(buffer);
            if (read <= 0)
            {
                break;
            }

            strategy.Append(buffer.AsSpan(0, read));

            //一次读取里可能有多条消息，也可能一条都凑不齐，必须循环取到取不动为止
            var parsed = 0;
            while (strategy.TryDequeue(out var payload))
            {
                received.Add(payload);
                parsed++;
            }

            trace.Add(new ReadTrace(trace.Count + 1, read, parsed, strategy.PendingBytes));
        }

        senderThread.Join();
        server?.Dispose();

        return new TransferResult(received, Interlocked.Read(ref bytesSent), trace);
    }
}
