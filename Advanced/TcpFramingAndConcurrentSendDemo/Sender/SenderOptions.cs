using FramingLib;

namespace Sender;

/// <summary>
/// 发送端参数。默认值刻意还原真实推送服务的形态：
/// 一个常驻线程按固定周期把攒下来的数据批量推出去，
/// 其中一条线程推大帧（批量数据），几条线程推小帧（心跳、单条通知）。
///
/// 小帧一次就能写完，本身是原子的；
/// 但它一旦插进大帧的分块之间，被毁掉的是那个大帧。
/// 这解释了一个反直觉的现象：出问题的总是最大的那类报文，
/// 而"肇事者"往往是最不起眼的心跳包。
///
/// 复现交织需要三个条件同时成立：
///   1. 帧大到必须分多次写出（BulkPayloadSize 远大于 WriteChunkSize）
///   2. 多个线程并发写同一个连接（SenderThreads）
///   3. 连接处于拥塞状态，写入被拆开（由接收端的 --read-delay-ms 放大）
/// 任一条件不满足都可能观察不到——这正是这类缺陷能潜伏很久的原因。
/// </summary>
public sealed record SenderOptions
{
    public int Port { get; init; } = 19869;

    public SendMode Mode { get; init; } = SendMode.Unsafe;

    /// <summary>
    /// 每轮推送的间隔。真实推送服务就是这个形态：
    /// 一个常驻线程按固定周期把攒下来的数据批量发出去
    /// </summary>
    public int IntervalMs { get; init; } = 1000;

    /// <summary>推送轮数，0 表示一直推到 Ctrl+C 为止，方便挂着断点慢慢调</summary>
    public int Rounds { get; init; }

    /// <summary>推大帧的线程数，模拟批量数据推送</summary>
    public int BulkSenderThreads { get; init; } = 1;

    public int BulkPayloadSize { get; init; } = 64 * 1024;

    /// <summary>每轮里每个大帧线程发多少帧</summary>
    public int BulkFramesPerThread { get; init; } = 4;

    /// <summary>推小帧的线程数，模拟心跳与高频小通知</summary>
    public int SmallSenderThreads { get; init; } = 3;

    public int SmallPayloadSize { get; init; } = 256;

    /// <summary>每轮里每个小帧线程发多少帧</summary>
    public int SmallFramesPerThread { get; init; } = 4;

    public int SendBufferSize { get; init; } = 8 * 1024;

    /// <summary>
    /// 单次写出的块大小，模拟内核在发送缓冲区满时把一帧拆成多次写入。
    /// 块越小、一帧被拆得越碎，被插队的概率越高
    /// </summary>
    public int WriteChunkSize { get; init; } = 8 * 1024;

    /// <summary>
    /// 发送超时。调试时在发送端停留过久，这个超时会让写入失败并判定连接报废；
    /// 需要长时间单步时可以用 --send-timeout-ms 调大
    /// </summary>
    public int SendTimeoutMs { get; init; } = 30_000;

    public long MaxPendingBytes { get; init; } = 32L * 1024 * 1024;

    public int BulkFramesPerRound => BulkSenderThreads * BulkFramesPerThread;

    public int SmallFramesPerRound => SmallSenderThreads * SmallFramesPerThread;

    public int FramesPerRound => BulkFramesPerRound + SmallFramesPerRound;

    public int ChunksPerBulkFrame
    {
        get
        {
            var frameSize = FrameCodec.HeaderLength + BulkPayloadSize + FrameCodec.TailLength;
            return (frameSize + WriteChunkSize - 1) / WriteChunkSize;
        }
    }

    public static SenderOptions Parse(string[] args)
    {
        var options = new SenderOptions();
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            var value = args[i + 1];
            options = args[i] switch
            {
                "--port" => options with { Port = int.Parse(value) },
                "--mode" => options with { Mode = ParseMode(value) },
                "--interval-ms" => options with { IntervalMs = int.Parse(value) },
                "--rounds" => options with { Rounds = int.Parse(value) },
                "--bulk-size" => options with { BulkPayloadSize = int.Parse(value) },
                "--bulk-frames" => options with { BulkFramesPerThread = int.Parse(value) },
                "--small-frames" => options with { SmallFramesPerThread = int.Parse(value) },
                "--chunk-size" => options with { WriteChunkSize = int.Parse(value) },
                "--send-timeout-ms" => options with { SendTimeoutMs = int.Parse(value) },
                _ => options
            };
        }
        return options;
    }

    private static SendMode ParseMode(string value) => value.ToLowerInvariant() switch
    {
        "unsafe" => SendMode.Unsafe,
        "queued" => SendMode.Queued,
        _ => throw new ArgumentException($"未知的发送模式：{value}，可选 unsafe / queued")
    };
}
