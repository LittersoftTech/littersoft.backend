namespace Pawfront.Application.Chat;

/// <summary>
/// What a message carries. Stored as the enum NAME on the Cosmos document, so
/// adding a kind is additive and old documents keep reading correctly.
/// </summary>
public enum ChatMessageKind
{
    /// <summary>Plain text. <c>Text</c> is required, <c>Attachment</c> is null.</summary>
    Text,

    /// <summary>
    /// An image. <c>Attachment</c> is required; <c>Text</c> may accompany it as a
    /// caption.
    /// </summary>
    Image
}

public static class ChatMessageKinds
{
    public static string ToWireValue(this ChatMessageKind kind) => kind switch
    {
        ChatMessageKind.Text => "Text",
        ChatMessageKind.Image => "Image",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown chat message kind.")
    };

    /// <summary>
    /// Tolerant on the way in, because this parses stored documents as well as
    /// request bodies: an unrecognised kind on an old document degrades to
    /// <see cref="ChatMessageKind.Text"/> rather than failing the whole history read.
    /// Request validation rejects unknown kinds separately, before they are stored.
    /// </summary>
    public static ChatMessageKind FromWireValue(string? value) =>
        string.Equals(value, "Image", StringComparison.OrdinalIgnoreCase)
            ? ChatMessageKind.Image
            : ChatMessageKind.Text;

    public static bool TryParseRequested(string? value, out ChatMessageKind kind)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "Text", StringComparison.OrdinalIgnoreCase))
        {
            // Omitted means text — the overwhelmingly common case, and it keeps the
            // send body to just { text } for an ordinary message.
            kind = ChatMessageKind.Text;
            return true;
        }

        if (string.Equals(value, "Image", StringComparison.OrdinalIgnoreCase))
        {
            kind = ChatMessageKind.Image;
            return true;
        }

        kind = ChatMessageKind.Text;
        return false;
    }
}
