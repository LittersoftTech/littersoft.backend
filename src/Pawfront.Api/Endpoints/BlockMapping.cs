using Pawfront.Application.Blocks;
using Pawfront.Contracts.Blocks;

namespace Pawfront.Api.Endpoints;

/// <summary>
/// Application block types to their wire shapes. Duplicated on each host that
/// exposes /blocks, matching how the support-ticket mapping is duplicated: the
/// three hosts share no endpoint assembly, and their <c>ApiResults</c> helpers
/// are their own.
/// </summary>
internal static class BlockMapping
{
    public static BlockedParticipantResponse ToResponse(ParticipantBlock block) =>
        new(
            block.BlockId,
            block.BlockedType.ToSqlValue(),
            block.BlockedId,
            block.BlockedName,
            block.BlockedBusinessName,
            block.BlockedPhotoUrl,
            block.Reason,
            block.CreatedAtUtc);

    public static BlockParticipantResponse ToResponse(BlockResult result) =>
        new(
            ToResponse(result.Block),
            result.WasAlreadyBlocked,
            result.CancelledCount,
            result.Failed.Select(job => new BlockUncancelledBookingResponse(
                job.BookingId,
                job.BookingType,
                job.JobId,
                job.Status,
                job.ErrorCode,
                job.Message)).ToList());

    public static BlockedParticipantsPageResponse ToResponse(ParticipantBlockPage page) =>
        new(
            page.Blocks.Select(ToResponse).ToList(),
            page.TotalCount,
            page.Skip,
            page.Take,
            page.HasMore);
}
