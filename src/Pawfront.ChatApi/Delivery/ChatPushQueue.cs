using System.Threading.Channels;
using Pawfront.Application.Chat;

namespace Pawfront.ChatApi.Delivery;

/// <summary>
/// In-process hand-off between a send and the FCM call it triggers.
///
/// A bounded channel, not an unbounded one: if pushes are being produced faster
/// than Firebase accepts them, an unbounded queue would swallow memory until the
/// host fell over. Bounded, the overflow is dropped and — because the outbox row
/// is already written and leased — the scheduled dispatcher sends it a minute
/// later instead. Degrading to "slightly late" beats degrading to "out of
/// memory".
///
/// Nothing here is durable, and nothing needs to be. Every item corresponds to a
/// committed outbox row; losing one costs latency, never the notification.
/// </summary>
internal sealed class ChatPushQueue : IChatPushDispatcher
{
    /// <summary>
    /// Sized for a burst, not a backlog. Reaching it means Firebase is slow or
    /// down, which is exactly when handing back to the retrying dispatcher is the
    /// right move.
    /// </summary>
    private const int Capacity = 1024;

    private readonly Channel<ChatPushWorkItem> channel =
        Channel.CreateBounded<ChatPushWorkItem>(new BoundedChannelOptions(Capacity)
        {
            // Drop rather than block: this runs on the sender's request thread,
            // and no chat message should ever wait on a notification queue.
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });

    private readonly ILogger<ChatPushQueue> logger;

    public ChatPushQueue(ILogger<ChatPushQueue> logger) => this.logger = logger;

    public ChannelReader<ChatPushWorkItem> Reader => channel.Reader;

    public void Enqueue(ChatPushWorkItem item)
    {
        if (!channel.Writer.TryWrite(item))
        {
            logger.LogWarning(
                "The chat push queue is full; notification {NotificationId} was not sent instantly and " +
                "will be delivered by the scheduled dispatcher once its lease lapses.",
                item.NotificationId);
        }
    }

    /// <summary>Lets the worker's shutdown drain what is already queued.</summary>
    public void Complete() => channel.Writer.TryComplete();
}
