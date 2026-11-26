using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using FramingLib;
using Sender;

var options = SenderOptions.Parse(args);

Console.WriteLine($"发送端　模式={options.Mode}　端口={options.Port}");
Console.WriteLine(new string('-', 70));
Console.WriteLine($"每轮：大帧 {options.BulkPayloadSize / 1024} KB × {options.BulkFramesPerRound} 帧"
                  + $"（{options.BulkSenderThreads} 线程）"
                  + $"，小帧 {options.SmallPayloadSize} B × {options.SmallFramesPerRound} 帧"
                  + $"（{options.SmallSenderThreads} 线程）");
Console.WriteLine($"推送间隔 {options.IntervalMs} ms，"
                  + (options.Rounds > 0 ? $"共 {options.Rounds} 轮" : "持续推送直到 Ctrl+C"));
Console.WriteLine($"单次写出块 {options.WriteChunkSize / 1024} KB，"
                  + $"一个大帧需分 {options.ChunksPerBulkFrame} 次写出");
Console.WriteLine(options.Mode == SendMode.Unsafe
    ? "当前为裸并发写：多线程直接写同一个 socket，大帧分块之间会被插队"
    : "当前为发送队列：业务线程只入队，实际写 socket 的只有一个线程");
Console.WriteLine(new string('-', 70));

using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
listener.Bind(new IPEndPoint(IPAddress.Loopback, options.Port));
listener.Listen(1);

Console.WriteLine("等待接收端连接...");
using var client = listener.Accept();
Console.WriteLine($"接收端已连接：{client.RemoteEndPoint}");
Console.WriteLine();

//刻意调小发送缓冲区，让连接更容易进入拥塞状态
client.SendBufferSize = options.SendBufferSize;

//没有发送超时，对端假死（TCP 窗口归零）会让发送线程永久挂住
client.SendTimeout = options.SendTimeoutMs;

IFrameSession session = options.Mode == SendMode.Unsafe
    ? new UnsafeSession(client, options.WriteChunkSize)
    : new QueuedSession(client, options.MaxPendingBytes, options.WriteChunkSize);

var cancelled = false;
Console.CancelKeyPress += (_, e) =>
{
    //先不让进程立刻退出，走完优雅关闭再结束
    e.Cancel = true;
    cancelled = true;
    Console.WriteLine();
    Console.WriteLine("收到 Ctrl+C，正在收尾...");
};

var sequence = 0;
var totalSent = 0;
var round = 0;

while (!cancelled && (options.Rounds <= 0 || round < options.Rounds))
{
    round++;

    //调试提示：断点打在这一行，每轮会停一次；
    //步入 SendOneRound 就能看到多个线程如何并发写同一个 socket
    var sentThisRound = SendOneRound(round);
    totalSent += sentThisRound;

    var pending = session.PendingBytes > 0
        ? $"，队列积压 {session.PendingBytes / 1024} KB"
        : string.Empty;
    Console.WriteLine($"[第 {round,3} 轮] 本轮 {sentThisRound} 帧，累计 {totalSent} 帧{pending}");

    if (cancelled || (options.Rounds > 0 && round >= options.Rounds))
    {
        break;
    }

    SleepInterruptible(options.IntervalMs);
}

Console.WriteLine();
Console.WriteLine($"推送结束：共 {round} 轮、{totalSent} 帧");

//异步发送队列必须等待真正写出，否则关连接时队列尾部的数据会被直接丢掉
session.Flush();

if (session is QueuedSession queued)
{
    Console.WriteLine($"队列峰值积压 {queued.PeakPendingBytes / 1024} KB"
                      + $"（上限 {options.MaxPendingBytes / 1024 / 1024} MB，超过即断开慢消费者）");
    if (queued.RejectedFrameCount > 0)
    {
        Console.WriteLine($"因背压被拒绝 {queued.RejectedFrameCount} 帧");
    }
}

session.Dispose();

//发 FIN 让接收端读到干净的流结束，而不是靠超时判断
try
{
    client.Shutdown(SocketShutdown.Send);
}
catch (SocketException)
{
}

Console.WriteLine("已关闭发送方向，等待接收端统计结果。");
return;

//一轮推送：多个线程同时往同一个连接写，交织就发生在这里
int SendOneRound(int roundIndex)
{
    var sentInRound = 0;

    Thread CreateSender(string name, int frameCount, int payloadSize) =>
        new(() =>
        {
            for (var i = 0; i < frameCount; i++)
            {
                var seq = Interlocked.Increment(ref sequence) - 1;
                var payload = PayloadPattern.Build(seq, payloadSize);

                //把负载大小写进 Type 字段，接收端据此区分大帧和小帧
                var frame = FrameCodec.Encode(type: payloadSize, payload);
                if (session.Send(frame))
                {
                    Interlocked.Increment(ref sentInRound);
                }
            }
        })
        {
            //线程名带上轮次，调试器的线程窗口里一眼能看出谁在写
            Name = $"{name}-R{roundIndex}",
            IsBackground = true
        };

    var threads = new List<Thread>();
    for (var i = 0; i < options.BulkSenderThreads; i++)
    {
        threads.Add(CreateSender($"Bulk{i}", options.BulkFramesPerThread, options.BulkPayloadSize));
    }
    for (var i = 0; i < options.SmallSenderThreads; i++)
    {
        threads.Add(CreateSender($"Small{i}", options.SmallFramesPerThread, options.SmallPayloadSize));
    }

    foreach (var thread in threads)
    {
        thread.Start();
    }
    foreach (var thread in threads)
    {
        thread.Join();
    }

    return sentInRound;
}

//分段睡眠，Ctrl+C 之后不用等满一个完整间隔
void SleepInterruptible(int totalMs)
{
    var sw = Stopwatch.StartNew();
    while (!cancelled && sw.ElapsedMilliseconds < totalMs)
    {
        Thread.Sleep(Math.Min(100, totalMs));
    }
}
