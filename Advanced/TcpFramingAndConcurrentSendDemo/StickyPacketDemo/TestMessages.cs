using System.Text;

namespace StickyPacketDemo;

/// <summary>
/// 构造一批刻意能触发各种边界情况的消息：
///   - 大量小消息：发送端连续写出，会被内核合并，接收端一次读到好几条 → 粘包
///   - 一条超大消息：远大于接收缓冲区，必然被拆成多次读取 → 半包
///   - 一条内容里含分隔符的消息：用来暴露"按分隔符分帧"方案的真实代价
/// </summary>
public static class TestMessages
{
    public const int SmallMessageGroups = 2;

    public const int SmallMessagesPerGroup = 20;

    public const int LargeMessageSize = 20_000;

    /// <summary>分隔符方案使用的结束符，同时也是下面那条"陷阱消息"里混入的字节</summary>
    public const byte Delimiter = (byte)'\n';

    public static IReadOnlyList<byte[]> Build()
    {
        var messages = new List<byte[]>();

        for (var i = 0; i < SmallMessagesPerGroup; i++)
        {
            messages.Add(Encoding.UTF8.GetBytes($"SMALL-{i:D4}-" + new string('a', 24)));
        }

        //内容里带换行：对长度前缀方案毫无影响，但会让分隔符方案误切成两条
        messages.Add(Encoding.UTF8.GetBytes("TRAP-0000-contains\nnewline-inside"));

        messages.Add(BuildLarge());

        for (var i = 0; i < SmallMessagesPerGroup; i++)
        {
            messages.Add(Encoding.UTF8.GetBytes($"SMALL-{i + SmallMessagesPerGroup:D4}-" + new string('b', 24)));
        }

        return messages;
    }

    public static int MaxLength(IReadOnlyList<byte[]> messages)
    {
        var max = 0;
        foreach (var m in messages)
        {
            if (m.Length > max)
            {
                max = m.Length;
            }
        }
        return max;
    }

    private static byte[] BuildLarge()
    {
        var sb = new StringBuilder(LargeMessageSize);
        sb.Append("LARGE-0000-");
        while (sb.Length < LargeMessageSize)
        {
            sb.Append((char)('0' + sb.Length % 10));
        }
        return Encoding.UTF8.GetBytes(sb.ToString(0, LargeMessageSize));
    }
}
