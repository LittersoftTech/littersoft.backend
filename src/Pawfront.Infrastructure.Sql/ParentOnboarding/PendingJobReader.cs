using Microsoft.Data.SqlClient;
using Pawfront.Application.ParentOnboarding;

namespace Pawfront.Infrastructure.Sql.ParentOnboarding;

/// <summary>
/// Reads one pending-job row. Shared by the two deletes that can be refused by
/// one — the account delete (<c>Parent.DeletePetParent</c>, result set 3) and the
/// per-pet delete (<c>Parent.DeletePetParentPet</c>, result set 2) — which
/// deliberately project the SAME columns in the SAME order so both refusals hand
/// the caller an identical payload. Change the column list in one sproc and you
/// must change it in the other and here.
/// </summary>
internal static class PendingJobReader
{
    public static PendingParentJob Read(SqlDataReader reader)
        => new(
            BookingId: reader.GetGuid(0),
            BookingType: reader.GetString(1),
            JobId: reader.GetString(2),
            ProviderId: reader.GetGuid(3),
            ProviderName: reader.IsDBNull(4) ? null : reader.GetString(4),
            ServiceCategory: reader.GetString(5),
            SubCategory: reader.GetString(6),
            Status: reader.GetString(7),
            ServiceDate: DateOnly.FromDateTime(reader.GetDateTime(8)),
            StartTime: reader.IsDBNull(9) ? null : TimeOnly.FromTimeSpan(reader.GetTimeSpan(9)),
            EndTime: reader.IsDBNull(10) ? null : TimeOnly.FromTimeSpan(reader.GetTimeSpan(10)),
            PetName: reader.IsDBNull(11) ? null : reader.GetString(11),
            ServiceId: reader.GetGuid(12),
            ServiceItemCode: reader.IsDBNull(13) ? null : reader.GetString(13),
            CheckOutDate: reader.IsDBNull(14) ? null : DateOnly.FromDateTime(reader.GetDateTime(14)),
            SnapshotUnitPrice: reader.IsDBNull(15) ? null : reader.GetDecimal(15));
}
