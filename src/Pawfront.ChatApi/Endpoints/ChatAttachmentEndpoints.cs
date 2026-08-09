using Pawfront.Application.Chat;
using Pawfront.Application.Storage;
using Pawfront.ChatApi.Auth;
using Pawfront.Contracts.Chat;

namespace Pawfront.ChatApi.Endpoints;

/// <summary>
/// Uploading an image to send in a chat.
///
/// A separate call from sending the message, and in the opposite order to review
/// photos: there the review must exist first because the blob path is keyed by
/// its id, whereas here the CONVERSATION is the key and it already exists. So the
/// client uploads, gets a url back, then sends a message of kind
/// <c>Image</c> carrying it.
///
/// That split also keeps the send path — the latency-sensitive one — free of
/// multipart parsing and a blob write.
/// </summary>
internal static class ChatAttachmentEndpoints
{
    /// <summary>
    /// 5 MB. Larger than the 3 MB the review and pet galleries allow, because a
    /// chat photo is usually straight from a camera and resizing it client-side
    /// before sending is a worse experience than a slightly slower upload.
    /// </summary>
    private const long MaxAttachmentBytes = 5 * 1024 * 1024;

    private static readonly HashSet<string> AllowedContentTypes =
        new(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/png", "image/webp" };

    public static IEndpointRouteBuilder MapChatAttachmentEndpoints(this IEndpointRouteBuilder builder)
    {
        builder.MapPost("/conversations/{conversationId:guid}/attachments", async (
            Guid conversationId,
            IFormFile? file,
            ICurrentChatParticipant currentParticipant,
            IChatService chatService,
            IPawfrontBlobStorage blobStorage,
            CancellationToken cancellationToken) =>
        {
            var (me, failure) = await ChatMapping.ResolveAsync(currentParticipant, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            if (Validate(file) is { } invalid)
            {
                return invalid;
            }

            try
            {
                // Authorise BEFORE writing anything. Without this, anyone could
                // push bytes into any conversation's blob folder by guessing an id.
                _ = await chatService.GetConversationAsync(conversationId, me!.Value, cancellationToken);
            }
            catch (Exception exception)
            {
                return ChatMapping.ToProblem(exception);
            }

            await using var stream = file!.OpenReadStream();

            var url = await blobStorage.UploadAsync(
                BlobUploadKind.ChatAttachment,
                // The conversation owns the path: chat-attachments/<conversationId>/<guid>.<ext>
                ownerId: conversationId,
                fileName: file.FileName,
                content: stream,
                contentType: file.ContentType,
                cancellationToken);

            // Echoed back so the client can put them straight on the message it is
            // about to send — the server does not re-measure the file at send time.
            return ApiResults.Ok(new ChatAttachmentPayload(
                url,
                file.ContentType,
                file.Length,
                Width: null,
                Height: null));
        })
        .DisableAntiforgery();

        return builder;
    }

    private static IResult? Validate(IFormFile? file)
    {
        if (file is null || file.Length == 0)
        {
            return ApiResults.BadRequest("InvalidFile", "An image file is required.");
        }

        if (file.Length > MaxAttachmentBytes)
        {
            return ApiResults.BadRequest(
                "ImageTooLarge", $"Attachment must be {MaxAttachmentBytes / (1024 * 1024)} MB or smaller.");
        }

        if (string.IsNullOrWhiteSpace(file.ContentType)
            || !AllowedContentTypes.Contains(file.ContentType))
        {
            return ApiResults.BadRequest(
                "UnsupportedImageFormat", "Attachment must be a JPEG, PNG, or WebP image.");
        }

        return null;
    }
}
