using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using FramingLib;
using Receiver;

var options = ReceiverOptions.Parse(args);

Console.WriteLine($"接收端　端口={options.Port}　每次读取后停顿={options.ReadDelayMs}ms");
Console.WriteLine("发送端按轮推送，这里持续接收；Ctrl+C 结束并输出统计");
Console.WriteLine(new string('-', 70));

using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
socket.ReceiveBufferSize = options.ReceiveBufferSize;
socket.ReceiveTimeout = options.ReceiveTimeoutMs;

if (!TryConnect(socket, options))
{
    Console.WriteLine($"连接失败：{options.ConnectRetrySeconds} 秒内未能连上发送端，请先启动 Sender。");
    return 1;
}

Console.WriteLine($"已连接发送端：{socket.RemoteEndPoint}");
Console.WriteLine("开始接收...");
Console.WriteLine();

var cancelled = false;
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancelled = true;
    Console.WriteLine();
    Console.WriteLine("收到 Ctrl+C，正在输出统计...");
    //主动关闭以唤醒阻塞中的 Receive
    try
    {
        socket.Shutdown(SocketShutdown.Both);
    }
    catch (SocketException)
    {
    }
};

var reader = new FrameReader();
var buffer = new byte[options.ReadChunkSize];
var receivedByType = new SortedDictionary<int, int>();
var totalReceived = 0;
var payloadMismatch = 0;
var reportedCorrupt = 0;
long totalBytes = 0;
var reportTimer = Stopwatch.StartNew();

while (!cancelled)
{
    int read;
    try
    {
        read = socket.Receive(buffer);
    }
    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
    {
        //超时不是错误：发送端可能正在等下一轮，也可能正停在断点上。
        //这里继续等待，否则调试时接收端会自己退出
        continue;
    }
    catch (SocketException ex)
    {
        Console.WriteLine($"接收中断：{ex.SocketErrorCode}");
        break;
    }
    catch (ObjectDisposedException)
    {
        break;//Ctrl+C 时 socket 已被关闭
    }

    if (read <= 0)
    {
        Console.WriteLine("发送端已关闭发送方向，接收结束。");
        break;
    }

    totalBytes += read;
    reader.Append(buffer.AsSpan(0, read));

    //一次读取里可能含有多帧（粘包），必须循环切到切不动为止
    while (reader.TryDequeue(out var frame))
    {
        totalReceived++;
        receivedByType.TryGetValue(frame.Type, out var count);
        receivedByType[frame.Type] = count + 1;

        if (!PayloadPattern.Verify(frame.Payload))
        {
            payloadMismatch++;
        }
    }

    //错帧是最值得关注的事件，一出现就立刻打出来，便于对照发送端那一轮
    if (reader.CorruptFrameCount > reportedCorrupt)
    {
        Console.WriteLine($"  !! 检测到错帧，累计 {reader.CorruptFrameCount} 个"
                          + $"（已丢弃 {reader.DiscardedByteCount:N0} 字节）");
        reportedCorrupt = reader.CorruptFrameCount;
    }

    if (reportTimer.ElapsedMilliseconds >= options.ReportIntervalMs)
    {
        Console.WriteLine($"  [累计] 完整 {totalReceived} 帧，错帧 {reader.CorruptFrameCount}，"
                          + $"收到 {totalBytes:N0} 字节");
        reportTimer.Restart();
    }

    if (options.ReadDelayMs > 0)
    {
        Thread.Sleep(options.ReadDelayMs);
    }
}

Console.WriteLine();
Console.WriteLine(new string('-', 70));
Console.WriteLine($"收到字节数　　　　{totalBytes:N0}");
Console.WriteLine($"完整接收帧数　　　{totalReceived}");
foreach (var (type, count) in receivedByType)
{
    //编码时把负载大小写进了 Type 字段
    var label = type >= 1024 ? $"{type / 1024} KB 帧" : $"{type} B 帧";
    Console.WriteLine($"　　其中 {label,-10}{count}");
}
Console.WriteLine($"错帧(结束符不符)　{reader.CorruptFrameCount}");
Console.WriteLine($"内容校验失败　　　{payloadMismatch}");
Console.WriteLine($"重同步丢弃字节　　{reader.DiscardedByteCount:N0}");
Console.WriteLine(new string('-', 70));

if (reader.CorruptFrameCount > 0 || payloadMismatch > 0)
{
    Console.WriteLine("检测到报文交织：发送端存在多线程并发写同一个 socket 的问题。");
    Console.WriteLine("受损的主要是大帧——只有大帧需要分多次写出，中途才会被其它线程插队；");
    Console.WriteLine("插进来的小帧随后也会在重新同步时被一起丢弃。");
    Console.WriteLine("用 --mode queued 重启 Sender 可以看到对比。");
    return 2;
}

Console.WriteLine("所有帧均完整且内容正确，未发现交织。");
return 0;

static bool TryConnect(Socket socket, ReceiverOptions options)
{
    var deadline = Stopwatch.StartNew();
    while (deadline.Elapsed < TimeSpan.FromSeconds(options.ConnectRetrySeconds))
    {
        try
        {
            socket.Connect(IPAddress.Loopback, options.Port);
            return true;
        }
        catch (SocketException)
        {
            Thread.Sleep(200);//发送端可能还没起来，稍后重试
        }
    }
    return false;
}
