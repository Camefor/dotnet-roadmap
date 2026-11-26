namespace Receiver;

/// <summary>
/// 接收端参数。
/// ReadDelayMs 是放大交织现象的关键：接收端消费得慢，
/// 发送端的内核缓冲区才会持续处于满的状态，大帧被拆得更碎
/// </summary>
public sealed record ReceiverOptions
{
    public int Port { get; init; } = 19869;

    public int ReceiveBufferSize { get; init; } = 8 * 1024;

    public int ReadChunkSize { get; init; } = 4 * 1024;

    /// <summary>每次读取后的停顿，模拟慢消费者</summary>
    public int ReadDelayMs { get; init; } = 1;

    /// <summary>
    /// 单次读取的等待上限。超时并不代表出错——
    /// 发送端正按间隔等待下一轮、或者正停在断点上时，这里本来就该等，
    /// 所以超时后只是继续等，不会结束接收
    /// </summary>
    public int ReceiveTimeoutMs { get; init; } = 5_000;

    /// <summary>累计统计的输出间隔</summary>
    public int ReportIntervalMs { get; init; } = 3_000;

    /// <summary>发送端可能还没启动，连接失败时重试的时长</summary>
    public int ConnectRetrySeconds { get; init; } = 60;

    public static ReceiverOptions Parse(string[] args)
    {
        var options = new ReceiverOptions();
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            var value = args[i + 1];
            options = args[i] switch
            {
                "--port" => options with { Port = int.Parse(value) },
                "--read-delay-ms" => options with { ReadDelayMs = int.Parse(value) },
                "--read-chunk" => options with { ReadChunkSize = int.Parse(value) },
                "--report-interval-ms" => options with { ReportIntervalMs = int.Parse(value) },
                _ => options
            };
        }
        return options;
    }
}
