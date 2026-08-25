using System.Data;
using Microsoft.Data.SqlClient;
using Pawfront.Application.Chat;
using Pawfront.Application.Configuration;
using Pawfront.Application.Support;

namespace Pawfront.Infrastructure.Sql.Chat;

/// <summary>
/// The thread index, read state and blocks, through the <c>Chat</c> schema's
/// procedures.
/// </summary>
/// <remarks>
/// Every <c>DATETIME2</c> is pinned to <see cref="TimeSpan.Zero"/> on the way
/// out. The column carries no offset, and the whole codebase stores UTC, so
/// letting <see cref="DateTimeOffset"/> infer the server's local offset would
/// silently shift every timestamp.
/// </remarks>
internal sealed class SqlChatConversationStore(
    string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider) : IChatConversationStore
{
    public async Task<ChatConversationDetail> GetOrCreateAsync(
        Guid providerId,
        Guid petParentId,
        ChatParticipant actor,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        Guid conversationId;

        await using (var command = StoredProcedure(connection, "[Chat].[GetOrCreateConversation]"))
        {
            command.Parameters.AddWithValue("@ProviderId", providerId);
            command.Parameters.AddWithValue("@PetParentId", petParentId);
            command.Parameters.AddWithValue("@ActorType", actor.Type.ToSqlValue());
            command.Parameters.AddWithValue("@ActorId", actor.Id);

            try
            {
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    throw new InvalidOperationException(
                        "Chat.GetOrCreateConversation returned no conversation row.");
                }

                conversationId = reader.GetGuid(0);
            }
            catch (SqlException exception) when (exception.Number == 51320)
            {
                throw new ChatCounterpartyNotFoundException(ChatParticipantType.Provider, providerId);
            }
            catch (SqlException exception) when (exception.Number == 51321)
            {
                throw new ChatProviderAccountDeletedException(providerId);
            }
            catch (SqlException exception) when (exception.Number == 51322)
            {
                throw new ChatCounterpartyNotFoundException(ChatParticipantType.PetParent, petParentId);
            }
            catch (SqlException exception) when (exception.Number == 51323)
            {
                throw new ChatBlockedException();
            }
            catch (SqlException exception) when (exception.Number == 51324)
            {
                throw new ChatForbiddenException(Guid.Empty);
            }
        }

        // A second read on the same connection rather than widening the create
        // procedure's output: the counterparty's name is a live join that
        // GetConversationForParticipant already owns, and duplicating it would be
        // two places to keep the "Deleted User" behaviour correct. Opening a
        // thread is rare, so the extra round trip costs nothing that matters.
        return await ReadDetailAsync(connection, conversationId, actor, cancellationToken)
            ?? throw new InvalidOperationException(
                "The conversation was created but could not be read back.");
    }

    public async Task<ChatConversationDetail?> GetForParticipantAsync(
        Guid conversationId,
        ChatParticipant participant,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        return await ReadDetailAsync(connection, conversationId, participant, cancellationToken);
    }

    public async Task<IReadOnlyList<ChatConversationCard>> ListAsync(
        ChatParticipant participant,
        string? search,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = StoredProcedure(connection, "[Chat].[ListConversations]");
        command.Parameters.AddWithValue("@ParticipantType", participant.Type.ToSqlValue());
        command.Parameters.AddWithValue("@ParticipantId", participant.Id);
        command.Parameters.AddWithValue("@Skip", skip);
        command.Parameters.AddWithValue("@Take", take);
        // DBNull, not null: AddWithValue(null) sends no value at all, which would
        // silently take the parameter's default rather than the intended "no
        // filter" — here they agree, but relying on that is how the two drift.
        command.Parameters.AddWithValue("@Search", (object?)search ?? DBNull.Value);

        var cards = new List<ChatConversationCard>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var conversation = new ChatConversation(
                ConversationId: reader.GetGuid(0),
                ProviderId: reader.GetGuid(1),
                PetParentId: reader.GetGuid(2),
                LastSequence: reader.GetInt64(3),
                LastMessageAtUtc: ReadUtc(reader, 4),
                LastMessagePreview: reader.IsDBNull(5) ? null : reader.GetString(5),
                LastMessageSenderType: reader.IsDBNull(6) ? null : reader.GetString(6),
                CreatedAtUtc: ReadUtc(reader, 7) ?? default,
                // The inbox list does not project UpdatedAtUtc — nothing on a card
                // uses it, and LastMessageAtUtc is what "recent" means here. Mirrored
                // from CreatedAtUtc rather than left default so the value is never
                // an implausible year 1.
                UpdatedAtUtc: ReadUtc(reader, 7) ?? default);

            var me = new ChatParticipantState(
                participant.Type,
                participant.Id,
                LastReadSequence: reader.GetInt64(8),
                UnreadCount: reader.GetInt32(9),
                IsMuted: reader.GetBoolean(10));

            var counterparty = new ChatCounterparty(
                ChatParticipantTypes.FromSqlValue(reader.GetString(11)),
                reader.GetGuid(12),
                Name: NullIfBlank(reader, 13),
                PhotoUrl: NullIfBlank(reader, 14),
                ServiceCategory: NullIfBlank(reader, 15));

            cards.Add(new ChatConversationCard(
                conversation,
                me,
                counterparty,
                MyTicket: null,
                // Appended after the service category, so nothing above moved.
                Block: new ChatBlockState(ReadFlag(reader, 16), ReadFlag(reader, 17))));
        }

        return cards;
    }

    public async Task<ChatParticipantState?> DeleteForParticipantAsync(
        Guid conversationId,
        ChatParticipant participant,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = StoredProcedure(connection, "[Chat].[DeleteConversationForParticipant]");
        command.Parameters.AddWithValue("@ConversationId", conversationId);
        command.Parameters.AddWithValue("@ParticipantType", participant.Type.ToSqlValue());
        command.Parameters.AddWithValue("@ParticipantId", participant.Id);

        SqlDataReader reader;
        try
        {
            reader = await command.ExecuteReaderAsync(cancellationToken);
        }
        catch (SqlException exception) when (exception.Number == 51352)
        {
            // Under legal hold by an open ticket. The procedure checks this only
            // after confirming the caller is a party, so a stranger still gets the
            // "not found" null below rather than learning a thread exists.
            throw new ConversationUnderLegalHoldException(conversationId);
        }

        await using (reader)
        {
            // No row = unknown conversation OR not the caller's. The procedure does
            // not distinguish them and neither does this.
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            return ReadParticipantState(reader);
        }
    }

    private static ChatParticipantState ReadParticipantState(SqlDataReader reader)
        => new ChatParticipantState(
            ChatParticipantTypes.FromSqlValue(reader.GetString(1)),
            reader.GetGuid(2),
            LastReadSequence: reader.GetInt64(3),
            UnreadCount: reader.GetInt32(4),
            IsMuted: ReadFlag(reader, 5),
            ClearedUpToSequence: reader.GetInt64(6),
            DeletedAtUtc: ReadUtc(reader, 7));

    public async Task<ChatMessageReservation> ReserveMessageAsync(
        Guid conversationId,
        ChatParticipant sender,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = StoredProcedure(connection, "[Chat].[ReserveMessageSequence]");
        command.Parameters.AddWithValue("@ConversationId", conversationId);
        command.Parameters.AddWithValue("@SenderType", sender.Type.ToSqlValue());
        command.Parameters.AddWithValue("@SenderId", sender.Id);
        command.Parameters.AddWithValue("@MessageId", messageId);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException(
                    "Chat.ReserveMessageSequence returned no reservation row.");
            }

            return new ChatMessageReservation(
                ConversationId: conversationId,
                MessageId: messageId,
                Sequence: reader.GetInt64(2),
                CreatedAtUtc: ReadUtc(reader, 3) ?? DateTimeOffset.UtcNow,
                IsReplay: ReadFlag(reader, 4),
                IsCommitted: ReadFlag(reader, 5));
        }
        catch (SqlException exception) when (exception.Number == 51325)
        {
            throw new ConversationNotFoundException(conversationId);
        }
        catch (SqlException exception) when (exception.Number == 51326)
        {
            throw new ChatForbiddenException(conversationId);
        }
        catch (SqlException exception) when (exception.Number == 51323)
        {
            throw new ChatBlockedException();
        }
    }

    public async Task<ChatAppendResult> CommitMessageAsync(
        Guid conversationId,
        Guid messageId,
        string preview,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = StoredProcedure(connection, "[Chat].[CommitMessageAppend]");
        command.Parameters.AddWithValue("@ConversationId", conversationId);
        command.Parameters.AddWithValue("@MessageId", messageId);
        command.Parameters.AddWithValue("@Preview", preview);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            // Result set 1 — who to deliver to, and whether a push was queued.
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Chat.CommitMessageAppend returned no recipient row.");
            }

            var recipientType = ChatParticipantTypes.FromSqlValue(reader.GetString(0));
            var recipientId = reader.GetGuid(1);
            var recipientUnread = reader.GetInt32(2);
            var recipientIsViewing = ReadFlag(reader, 3);
            var recipientIsMuted = ReadFlag(reader, 4);
            var notificationId = reader.IsDBNull(5) ? (Guid?)null : reader.GetGuid(5);
            // The template parameters the enqueue wrote. Handed back so the push
            // can be rendered from the C# catalog without re-reading the row.
            var notificationDataJson = reader.IsDBNull(6) ? null : reader.GetString(6);
            var sequence = reader.GetInt64(7);
            var createdAtUtc = ReadUtc(reader, 8) ?? DateTimeOffset.UtcNow;

            // Result set 2 — the devices to push to. Empty when no notification
            // was queued, so no branch is needed here.
            var tokens = new List<string>();
            if (await reader.NextResultAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (!reader.IsDBNull(0))
                    {
                        tokens.Add(reader.GetString(0));
                    }
                }
            }

            return new ChatAppendResult(
                conversationId,
                messageId,
                sequence,
                createdAtUtc,
                recipientType,
                recipientId,
                recipientUnread,
                recipientIsViewing,
                recipientIsMuted,
                notificationId,
                tokens,
                notificationDataJson);
        }
        catch (SqlException exception) when (exception.Number is 51325 or 51328)
        {
            throw new ConversationNotFoundException(conversationId);
        }
    }

    public async Task ReleaseMessageReservationAsync(
        Guid conversationId,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = StoredProcedure(connection, "[Chat].[ReleaseMessageReservation]");
        command.Parameters.AddWithValue("@ConversationId", conversationId);
        command.Parameters.AddWithValue("@MessageId", messageId);

        // The procedure never THROWs and its one result set is diagnostic only —
        // whether a row was there to release changes nothing the caller does.
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RefreshDeletedMessagePreviewAsync(
        Guid conversationId,
        long sequence,
        string preview,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = StoredProcedure(connection, "[Chat].[RefreshDeletedMessagePreview]");
        command.Parameters.AddWithValue("@ConversationId", conversationId);
        command.Parameters.AddWithValue("@Sequence", sequence);
        command.Parameters.AddWithValue("@Preview", preview);

        // No result set: the guarded UPDATE either applied or a newer message had
        // already moved the preview on, and neither changes what the caller does.
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ChatParticipantState?> MarkReadAsync(
        Guid conversationId,
        ChatParticipant participant,
        long upToSequence,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = StoredProcedure(connection, "[Chat].[MarkConversationRead]");
        command.Parameters.AddWithValue("@ConversationId", conversationId);
        command.Parameters.AddWithValue("@ParticipantType", participant.Type.ToSqlValue());
        command.Parameters.AddWithValue("@ParticipantId", participant.Id);
        command.Parameters.AddWithValue("@UpToSequence", upToSequence);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ChatParticipantState(
            ChatParticipantTypes.FromSqlValue(reader.GetString(1)),
            reader.GetGuid(2),
            LastReadSequence: reader.GetInt64(3),
            UnreadCount: reader.GetInt32(4),
            IsMuted: reader.GetBoolean(5));
    }

    public async Task<ChatUnreadSummary> GetUnreadSummaryAsync(
        ChatParticipant participant,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = StoredProcedure(connection, "[Chat].[GetUnreadSummary]");
        command.Parameters.AddWithValue("@ParticipantType", participant.Type.ToSqlValue());
        command.Parameters.AddWithValue("@ParticipantId", participant.Id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        // The procedure always emits exactly one row, zeros included, so a caller
        // never has to handle "no rows".
        return await reader.ReadAsync(cancellationToken)
            ? new ChatUnreadSummary(reader.GetInt32(0), reader.GetInt32(1))
            : new ChatUnreadSummary(0, 0);
    }

    // Blocking moved out of this store to SqlBlockStore when a block stopped
    // being a chat remedy -- the table now lives in the [Block] schema, and the
    // three Chat.*BlockParticipant procedures are dropped on deploy. This store
    // still depends on blocks (GetOrCreateConversation and ReserveMessageSequence
    // both refuse a blocked pair, and both conversation reads project the flags),
    // it just no longer owns them.

    private static async Task<ChatConversationDetail?> ReadDetailAsync(
        SqlConnection connection,
        Guid conversationId,
        ChatParticipant participant,
        CancellationToken cancellationToken)
    {
        await using var command = StoredProcedure(connection, "[Chat].[GetConversationForParticipant]");
        command.Parameters.AddWithValue("@ConversationId", conversationId);
        command.Parameters.AddWithValue("@ParticipantType", participant.Type.ToSqlValue());
        command.Parameters.AddWithValue("@ParticipantId", participant.Id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var conversation = new ChatConversation(
            ConversationId: reader.GetGuid(0),
            ProviderId: reader.GetGuid(1),
            PetParentId: reader.GetGuid(2),
            LastSequence: reader.GetInt64(3),
            LastMessageAtUtc: ReadUtc(reader, 4),
            LastMessagePreview: reader.IsDBNull(5) ? null : reader.GetString(5),
            LastMessageSenderType: reader.IsDBNull(6) ? null : reader.GetString(6),
            CreatedAtUtc: ReadUtc(reader, 7) ?? default,
            UpdatedAtUtc: ReadUtc(reader, 8) ?? default);

        var me = new ChatParticipantState(
            participant.Type,
            participant.Id,
            LastReadSequence: reader.GetInt64(9),
            UnreadCount: reader.GetInt32(10),
            IsMuted: ReadFlag(reader, 11),
            // The caller's "delete chat" watermark. This is the ONLY read that
            // projects it, and it is the one the history filter runs off — see
            // ChatService.GetHistoryAsync.
            ClearedUpToSequence: reader.GetInt64(12),
            DeletedAtUtc: ReadUtc(reader, 13));

        var counterparty = new ChatCounterparty(participant.Type.Counterparty(), Guid.Empty, null, null);

        // Whether a block closes this thread. It rides the counterparty result set
        // because that is the SELECT it was appended to, not because it describes
        // the counterparty — so it is read here and returned on the detail itself.
        // Defaults to "not blocked" if that set is somehow absent, which is the
        // safe direction: the send path is gated in SQL regardless, so at worst
        // the app offers a composer whose send is then refused.
        ChatBlockState? block = null;

        if (await reader.NextResultAsync(cancellationToken)
            && await reader.ReadAsync(cancellationToken))
        {
            counterparty = new ChatCounterparty(
                ChatParticipantTypes.FromSqlValue(reader.GetString(0)),
                reader.GetGuid(1),
                Name: NullIfBlank(reader, 2),
                PhotoUrl: NullIfBlank(reader, 3),
                // Ordinal 4 is CounterpartyIsDeleted, which this reader does not use.
                ServiceCategory: NullIfBlank(reader, 5));

            block = new ChatBlockState(ReadFlag(reader, 6), ReadFlag(reader, 7));
        }

        return new ChatConversationDetail(conversation, me, counterparty, MyTicket: null, Block: block);
    }

    private static string? NullIfBlank(SqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = reader.GetString(ordinal).Trim();
        return value.Length == 0 ? null : value;
    }

    /// <summary>
    /// Reads a boolean flag without caring whether SQL typed the column
    /// <c>BIT</c> or <c>INT</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="SqlDataReader.GetBoolean"/> is exact: on an <c>INT</c> column it
    /// throws <see cref="InvalidCastException"/> rather than coercing. That is how
    /// the retired <c>Chat.AppendMessage</c> broke every send in the product — it
    /// projected <c>COALESCE(@RecipientIsMuted, 0)</c>, and because <c>INT</c>
    /// outranks <c>BIT</c> in data type precedence, a flag that reads as a
    /// perfectly ordinary <c>0</c> came back typed <c>INT</c> and this reader threw
    /// AFTER the procedure had committed.
    ///
    /// The procedures now <c>CAST(... AS BIT)</c>, which is the real fix. This
    /// exists so that the same slip can never again take down a write path: a
    /// widened flag costs a coercion here instead of a 500 with the work already
    /// done.
    /// </remarks>
    private static bool ReadFlag(SqlDataReader reader, int ordinal) =>
        !reader.IsDBNull(ordinal) && Convert.ToBoolean(reader.GetValue(ordinal));

    /// <summary>
    /// Pins the offset to zero. The column is <c>DATETIME2</c> and carries none,
    /// and everything in this codebase is stored UTC — inferring the server's
    /// local offset here would shift every timestamp by it.
    /// </summary>
    private static DateTimeOffset? ReadUtc(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : new DateTimeOffset(reader.GetDateTime(ordinal), TimeSpan.Zero);

    private static SqlCommand StoredProcedure(SqlConnection connection, string name) =>
        new(name, connection) { CommandType = CommandType.StoredProcedure };

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
