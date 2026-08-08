using Microsoft.Data.SqlClient;
using Pawfront.Application.Bookings;
using Pawfront.Application.Configuration;
using Pawfront.Application.Earnings;
using Pawfront.Application.Reviews;

namespace Pawfront.Infrastructure.Sql.Reviews;

/// <summary>
/// Reads and writes booking reviews through the <c>Review</c> schema's procedures.
/// Also serves <see cref="IPetParentRatingReader"/>: both sides read the same table
/// and there is no reason to open a second store for one aggregate.
/// </summary>
/// <remarks>
/// <c>Rating</c> is <c>TINYINT</c> in SQL, so it is read with <c>GetByte</c> — a
/// <c>GetInt32</c> against it throws <see cref="InvalidCastException"/> at runtime,
/// which is the sort of thing a parse-clean deploy will not catch.
/// </remarks>
internal sealed class SqlBookingReviewStore(
    string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider) : IBookingReviewStore, IPetParentRatingReader
{
    public async Task<BookingReviewRecord> UpsertAsync(
        SubmitBookingReviewCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var sqlCommand = new SqlCommand("[Review].[UpsertBookingReview]", connection)
        {
            CommandType = System.Data.CommandType.StoredProcedure
        };
        sqlCommand.Parameters.AddWithValue("@BookingType", command.BookingType);
        sqlCommand.Parameters.AddWithValue("@BookingId", command.BookingId);
        sqlCommand.Parameters.AddWithValue("@ReviewerType", command.ReviewerType);
        sqlCommand.Parameters.AddWithValue("@ActorId", command.ActorId);
        sqlCommand.Parameters.AddWithValue("@Rating", (byte)command.Rating);
        sqlCommand.Parameters.AddWithValue(
            "@Comment", command.Comment is null ? DBNull.Value : command.Comment);

        try
        {
            await using var reader = await sqlCommand.ExecuteReaderAsync(cancellationToken);
            var record = await ReadReviewWithPhotosAsync(reader, cancellationToken);

            // The sproc always emits the row it just wrote, so a null here would mean
            // the procedure changed shape rather than anything a caller can provoke.
            return record ?? throw new InvalidOperationException(
                "Review.UpsertBookingReview returned no review row.");
        }
        catch (SqlException exception) when (exception.Number == 51300)
        {
            throw command.BookingType == ReviewedBookingTypes.NightStay
                ? new NightStayBookingNotFoundException(command.BookingId)
                : new BookingNotFoundException(command.BookingId);
        }
        catch (SqlException exception) when (exception.Number == 51301)
        {
            throw new ReviewForbiddenException(command.BookingId);
        }
        catch (SqlException exception) when (exception.Number == 51302)
        {
            throw new BookingNotReviewableException(command.BookingId);
        }
        catch (SqlException exception) when (exception.Number == 51303)
        {
            throw new ReviewNotAppBookingException(command.BookingId);
        }
        catch (SqlException exception) when (exception.Number == 51304)
        {
            throw new ArgumentException("Invalid review request.", nameof(command));
        }
    }

    public async Task<BookingReviewRecord?> GetAsync(
        string bookingType,
        Guid bookingId,
        string reviewerType,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("[Review].[GetBookingReview]", connection)
        {
            CommandType = System.Data.CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@BookingType", bookingType);
        command.Parameters.AddWithValue("@BookingId", bookingId);
        command.Parameters.AddWithValue("@ReviewerType", reviewerType);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await ReadReviewWithPhotosAsync(reader, cancellationToken);
    }

    public async Task<BookingReviewPhotoRecord> AddPhotoAsync(
        Guid bookingReviewId,
        Guid petParentId,
        string photoUrl,
        int maxPhotos,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("[Review].[AddBookingReviewPhoto]", connection)
        {
            CommandType = System.Data.CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@BookingReviewId", bookingReviewId);
        command.Parameters.AddWithValue("@PetParentId", petParentId);
        command.Parameters.AddWithValue("@PhotoUrl", photoUrl);
        command.Parameters.AddWithValue("@MaxPhotos", maxPhotos);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException(
                    "Review.AddBookingReviewPhoto returned no photo row.");
            }

            return ReadPhoto(reader);
        }
        catch (SqlException exception) when (exception.Number == 51305)
        {
            throw new BookingReviewNotFoundException(bookingReviewId);
        }
        catch (SqlException exception) when (exception.Number == 51306)
        {
            throw new ReviewPhotoLimitReachedException(bookingReviewId, maxPhotos);
        }
    }

    public async Task<DeletedBookingReviewPhoto> DeletePhotoAsync(
        Guid bookingReviewId,
        Guid bookingReviewPhotoId,
        Guid petParentId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("[Review].[DeleteBookingReviewPhoto]", connection)
        {
            CommandType = System.Data.CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@BookingReviewId", bookingReviewId);
        command.Parameters.AddWithValue("@BookingReviewPhotoId", bookingReviewPhotoId);
        command.Parameters.AddWithValue("@PetParentId", petParentId);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new BookingReviewPhotoNotFoundException(bookingReviewPhotoId);
            }

            return new DeletedBookingReviewPhoto(
                BookingReviewPhotoId: reader.GetGuid(0),
                BookingReviewId: reader.GetGuid(1),
                PhotoUrl: reader.GetString(2),
                DeletedAtUtc: new DateTimeOffset(reader.GetDateTime(3), TimeSpan.Zero));
        }
        catch (SqlException exception) when (exception.Number == 51307)
        {
            throw new BookingReviewPhotoNotFoundException(bookingReviewPhotoId);
        }
    }

    public async Task<ProviderReviewListResult> ListForProviderAsync(
        ProviderReviewQuery query,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("[Review].[ListProviderReviews]", connection)
        {
            CommandType = System.Data.CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@ProviderId", query.ProviderId);
        command.Parameters.AddWithValue(
            "@SortBy", query.SortBy == ReviewSortBy.Rating ? "Rating" : "Date");
        command.Parameters.AddWithValue(
            "@SortDirection", query.SortDirection == EarningsSortDirection.Ascending ? "Asc" : "Desc");
        command.Parameters.AddWithValue("@Skip", query.Skip);
        command.Parameters.AddWithValue("@Take", query.Take);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        // Result set 1: the whole-population summary.
        var summary = ReviewSummary.Empty;
        if (await reader.ReadAsync(cancellationToken))
        {
            summary = new ReviewSummary(
                ReviewCount: reader.GetInt32(0),
                AverageRating: reader.IsDBNull(1) ? null : reader.GetDecimal(1),
                FiveStar: reader.GetInt32(2),
                FourStar: reader.GetInt32(3),
                ThreeStar: reader.GetInt32(4),
                TwoStar: reader.GetInt32(5),
                OneStar: reader.GetInt32(6));
        }

        // Result set 2: the ordered page. Held by id so result set 3's photos can be
        // attached without re-querying.
        var rows = new List<ProviderReviewRow>();
        if (await reader.NextResultAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new ProviderReviewRow(
                    BookingReviewId: reader.GetGuid(0),
                    BookingType: reader.GetString(1),
                    BookingId: reader.GetGuid(2),
                    JobNumber: reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    PetParentId: reader.GetGuid(4),
                    ParentName: reader.IsDBNull(5) ? null : reader.GetString(5),
                    ParentPhotoUrl: reader.IsDBNull(6) ? null : reader.GetString(6),
                    Rating: reader.GetByte(7),
                    Comment: reader.IsDBNull(8) ? null : reader.GetString(8),
                    PhotoUrls: [],
                    CreatedAtUtc: new DateTimeOffset(reader.GetDateTime(9), TimeSpan.Zero),
                    UpdatedAtUtc: new DateTimeOffset(reader.GetDateTime(10), TimeSpan.Zero)));
            }
        }

        // Result set 3: every photo on the page, pre-joined so a page of reviews costs
        // one round trip rather than one per review.
        var photosByReview = new Dictionary<Guid, List<string>>();
        if (await reader.NextResultAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var reviewId = reader.GetGuid(1);
                if (!photosByReview.TryGetValue(reviewId, out var urls))
                {
                    urls = [];
                    photosByReview[reviewId] = urls;
                }

                urls.Add(reader.GetString(2));
            }
        }

        var items = rows
            .Select(row => photosByReview.TryGetValue(row.BookingReviewId, out var urls)
                ? row with { PhotoUrls = urls }
                : row)
            .ToArray();

        return new ProviderReviewListResult(summary, items, query.Skip, query.Take);
    }

    public async Task<PetParentRatingSummary> GetAsync(
        Guid petParentId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("[Review].[GetPetParentRatingSummary]", connection)
        {
            CommandType = System.Data.CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@PetParentId", petParentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return PetParentRatingSummary.Empty;
        }

        return new PetParentRatingSummary(
            RatingCount: reader.GetInt32(0),
            AverageRating: reader.IsDBNull(1) ? null : reader.GetDecimal(1));
    }

    /// <summary>
    /// Reads the shared (review row, photos) two-result-set shape emitted by both
    /// <c>UpsertBookingReview</c> and <c>GetBookingReview</c>. An empty first result
    /// set means "no review written yet", which is an ordinary answer for the get.
    /// </summary>
    private static async Task<BookingReviewRecord?> ReadReviewWithPhotosAsync(
        SqlDataReader reader,
        CancellationToken cancellationToken)
    {
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var record = new BookingReviewRecord(
            BookingReviewId: reader.GetGuid(0),
            BookingType: reader.GetString(1),
            BookingId: reader.GetGuid(2),
            ReviewerType: reader.GetString(3),
            ProviderId: reader.GetGuid(4),
            PetParentId: reader.GetGuid(5),
            Rating: reader.GetByte(6),
            Comment: reader.IsDBNull(7) ? null : reader.GetString(7),
            Photos: [],
            CreatedAtUtc: new DateTimeOffset(reader.GetDateTime(8), TimeSpan.Zero),
            UpdatedAtUtc: new DateTimeOffset(reader.GetDateTime(9), TimeSpan.Zero));

        var photos = new List<BookingReviewPhotoRecord>();
        if (await reader.NextResultAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                photos.Add(ReadPhoto(reader));
            }
        }

        return record with { Photos = photos };
    }

    private static BookingReviewPhotoRecord ReadPhoto(SqlDataReader reader)
        => new(
            BookingReviewPhotoId: reader.GetGuid(0),
            BookingReviewId: reader.GetGuid(1),
            PhotoUrl: reader.GetString(2),
            CreatedAtUtc: new DateTimeOffset(reader.GetDateTime(3), TimeSpan.Zero));

    private async Task<string> GetConnectionStringAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(configuredConnectionString))
        {
            return configuredConnectionString;
        }

        if (secretProvider is null)
        {
            throw new InvalidOperationException(
                "SQL Server connection string is not configured and no secret provider is registered.");
        }

        return await secretProvider.GetSqlConnectionStringAsync(cancellationToken);
    }
}
