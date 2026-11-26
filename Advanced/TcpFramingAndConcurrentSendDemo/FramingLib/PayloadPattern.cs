using System.Buffers.Binary;

namespace FramingLib;

/// <summary>
/// 构造可自校验的负载，用来证明"帧切对了，内容也没串"。
///
/// 负载结构：序号(4 字节) + 用该序号填充的重复字节。
/// 每一帧的填充字节都由自己的序号决定，一旦两帧的字节在传输中交织，
/// 填充区就会出现不属于本帧序号的字节，校验立刻失败。
/// 这一层是对结束符校验的补充：结束符证明边界对，这里证明内容对。
/// </summary>
public static class PayloadPattern
{
    public static byte[] Build(int sequence, int size)
    {
        var payload = new byte[size];
        BinaryPrimitives.WriteInt32LittleEndian(payload, sequence);
        payload.AsSpan(4).Fill(FillOf(sequence));
        return payload;
    }

    public static bool Verify(byte[] payload)
    {
        if (payload.Length < 4)
        {
            return false;
        }

        var sequence = BinaryPrimitives.ReadInt32LittleEndian(payload);
        var expected = FillOf(sequence);
        foreach (var b in payload.AsSpan(4))
        {
            if (b != expected)
            {
                return false;
            }
        }
        return true;
    }

    private static byte FillOf(int sequence) => (byte)(sequence % 251);
}
