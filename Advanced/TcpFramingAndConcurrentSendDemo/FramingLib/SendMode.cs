namespace FramingLib;

public enum SendMode
{
    /// <summary>多线程直接并发写 socket，会交织</summary>
    Unsafe,

    /// <summary>发送队列 + 专属发送线程，串行写出</summary>
    Queued
}
