using StickyPacketDemo;

const int ReadBufferSize = 4096;
const int StartDelayMs = 200;

var messages = TestMessages.Build();
var maxLength = TestMessages.MaxLength(messages);
var usefulBytes = messages.Sum(m => (long)m.Length);

Console.WriteLine("TCP 粘包 / 半包与分帧方案 Demo");
Console.WriteLine(new string('-', 78));
Console.WriteLine($"待发送 {messages.Count} 条消息，有效负载合计 {usefulBytes:N0} 字节");
Console.WriteLine($"其中 {TestMessages.SmallMessagesPerGroup * TestMessages.SmallMessageGroups} 条小消息"
                  + $"、1 条 {TestMessages.LargeMessageSize:N0} 字节大消息"
                  + $"、1 条内容里带换行的陷阱消息");
Console.WriteLine($"接收端每次最多读 {ReadBufferSize} 字节，并先停顿 {StartDelayMs}ms 让数据堆积");
Console.WriteLine();

//先用长度前缀方案跑一遍，把读取边界与消息边界的错位关系显示出来
var showcase = Run(new LengthPrefixFramer());
PrintTrace(showcase.Result);

var results = new List<Outcome>
{
    Run(new NaiveFramer()),
    Run(new FixedLengthFramer(maxLength)),
    Run(new DelimiterFramer(TestMessages.Delimiter)),
    showcase
};

Console.WriteLine();
Console.WriteLine("四种处理方式对比");
Console.WriteLine(new string('-', 78));
Console.WriteLine($"{"方案",-16}{"解析条数",-12}{"内容正确",-12}{"发送字节",-14}{"放大",-8}");
Console.WriteLine(new string('-', 78));
foreach (var outcome in results)
{
    var ratio = $"{outcome.Result.BytesSent / (double)usefulBytes:F2}x";
    var count = $"{outcome.Result.Received.Count}/{messages.Count}";
    var correct = $"{outcome.CorrectCount}/{messages.Count}";
    Console.WriteLine($"{outcome.Strategy.Name,-16}{count,-12}{correct,-12}{outcome.Result.BytesSent,-14:N0}{ratio,-8}");
}
Console.WriteLine(new string('-', 78));
foreach (var outcome in results)
{
    Console.WriteLine($"{outcome.Strategy.Name,-16}{outcome.Strategy.Note}");
}

Console.WriteLine();
Console.WriteLine("要点");
Console.WriteLine("1. 粘包/半包不丢数据：字节一个不少地到达，只是边界要自己划。");
Console.WriteLine("   边界划对了就能 100% 还原，上面长度前缀方案就是 42/42。");
Console.WriteLine("2. 朴素方案解析出的条数等于 Receive 次数，和消息数毫无关系。");
Console.WriteLine("3. 分隔符方案被陷阱消息切成两条后，后续消息全部错位，");
Console.WriteLine("   一次误切会让整条流失去同步——这就是它不二进制安全的代价。");
Console.WriteLine("4. 固定长度方案结果正确，但要按最大消息补齐，这里放大了近 40 倍。");
Console.WriteLine();
Console.WriteLine("对照另一个 Demo（Sender/Receiver 并发写交织）：");
Console.WriteLine("那里字节流本身就被写错了，分帧做得再好也还原不出来，必然丢数据。");
Console.WriteLine("所以两者症状相似，病根完全不同：粘包修接收端，交织必须修发送端。");

Outcome Run(IFramingStrategy strategy)
{
    var result = LoopbackTransfer.Run(strategy, messages, ReadBufferSize, StartDelayMs);
    var correct = 0;
    var limit = Math.Min(result.Received.Count, messages.Count);
    for (var i = 0; i < limit; i++)
    {
        if (result.Received[i].AsSpan().SequenceEqual(messages[i]))
        {
            correct++;
        }
    }
    return new Outcome(strategy, result, correct);
}

static void PrintTrace(TransferResult result)
{
    Console.WriteLine("每次 Receive 的实际情况（长度前缀方案）");
    Console.WriteLine(new string('-', 78));
    Console.WriteLine($"{"第几次读",-10}{"读到字节",-12}{"解析出消息",-14}{"半包残留",-12}{"现象",-10}");
    Console.WriteLine(new string('-', 78));

    foreach (var t in result.Trace)
    {
        var phenomenon = t.MessagesParsed switch
        {
            0 => "半包",//一条消息都凑不齐，说明大消息还没收完
            1 => "",
            _ => "粘包"//一次读取里就含有多条完整消息
        };
        Console.WriteLine($"{t.Index,-10}{t.ReadBytes,-12}{t.MessagesParsed,-14}{t.PendingBytes,-12}{phenomenon,-10}");
    }

    Console.WriteLine(new string('-', 78));
    Console.WriteLine("可以看到：读取边界和消息边界毫无关系。");
    Console.WriteLine("一次读取可能横跨多条消息（粘包），也可能连一条都凑不齐（半包）。");
    Console.WriteLine();
}

internal sealed record Outcome(IFramingStrategy Strategy, TransferResult Result, int CorrectCount);
