using System.Data;
using Microsoft.Data.SqlClient;
using Pawfront.Application.Chat;
using Pawfront.Application.Configuration;

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
                PhotoUrl: NullIfBlank(reader, 14));

            cards.Add(new ChatConversationCard(conversation, me, counterparty));
        }

        return cards;
    }

    public async Task<ChatAppendResult> AppendMessageAsync(
        Guid conversationId,
        ChatParticipant sender,
        Guid messageId,
        string preview,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = StoredProcedure(connection, "[Chat].[AppendMessage]");
        command.Parameters.AddWithValue("@ConversationId", conversationId);
        command.Parameters.AddWithValue("@SenderType", sender.Type.ToSqlValue());
        command.Parameters.AddWithValue("@SenderId", sender.Id);
        command.Parameters.AddWithValue("@MessageId", messageId);
        command.Parameters.AddWithValue("@Preview", preview);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            // Result set 1 — the sequence this message was given.
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Chat.AppendMessage returned no message row.");
            }

            var sequence = reader.GetInt64(1);
            var createdAtUtc = ReadUtc(reader, 2) ?? DateTimeOffset.UtcNow;

            // Result set 2 — who to deliver to, and whether a push was queued.
            if (!await reader.NextResultAsync(cancellationToken)
                || !await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Chat.AppendMessage returned no recipient row.");
            }

            var recipientType = ChatParticipantTypes.FromSqlValue(reader.GetString(0));
            var recipientId = reader.GetGuid(1);
            var recipientUnread = reader.GetInt32(2);
            var recipientIsViewing = reader.GetBoolean(3);
            var recipientIsMuted = reader.GetBoolean(4);
            var notificationId = reader.IsDBNull(5) ? (Guid?)null : reader.GetGuid(5);
            // The template parameters the enqueue wrote. Handed back so the push
            // can be rendered from the C# catalog without re-reading the row.
            var notificationDataJson = reader.IsDBNull(6) ? null : reader.GetString(6);

            // Result set 3 — the devices to push to. Empty when no notification
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

    public async Task<ChatBlock> BlockAsync(
        ChatParticipant blocker,
        ChatParticipantType blockedType,
        Guid blockedId,
        string? reason,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = StoredProcedure(connection, "[Chat].[BlockChatParticipant]");
        command.Parameters.AddWithValue("@BlockerType", blocker.Type.ToSqlValue());
        command.Parameters.AddWithValue("@BlockerId", blocker.Id);
        command.Parameters.AddWithValue("@BlockedType", blockedType.ToSqlValue());
        command.Parameters.AddWithValue("@BlockedId", blockedId);
        command.Parameters.AddWithValue("@Reason", (object?)reason ?? DBNull.Value);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            return await reader.ReadAsync(cancellationToken)
                ? ReadBlock(reader, hasNameColumns: false)
                : throw new InvalidOperationException("Chat.BlockChatParticipant returned no row.");
        }
        catch (SqlException exception) when (exception.Number == 51327)
        {
            throw new ChatInvalidBlockException();
        }
    }

    public async Task<ChatBlock?> UnblockAsync(
        Guid chatBlockId,
        ChatParticipant blocker,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = StoredProcedure(connection, "[Chat].[UnblockChatParticipant]");
        command.Parameters.AddWithValue("@ChatBlockId", chatBlockId);
        command.Parameters.AddWithValue("@BlockerType", blocker.Type.ToSqlValue());
        command.Parameters.AddWithValue("@BlockerId", blocker.Id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        // No rows means unknown id OR somebody else's block — one case by design,
        // so a block id cannot be probed for existence.
        return await reader.ReadAsync(cancellationToken)
            ? ReadBlock(reader, hasNameColumns: false)
            : null;
    }

    public async Task<IReadOnlyList<ChatBlock>> ListBlocksAsync(
        ChatParticipant blocker,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = StoredProcedure(connection, "[Chat].[ListBlockedParticipants]");
        command.Parameters.AddWithValue("@BlockerType", blocker.Type.ToSqlValue());
        command.Parameters.AddWithValue("@BlockerId", blocker.Id);

        var blocks = new List<ChatBlock>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            blocks.Add(ReadBlock(reader, hasNameColumns: true));
        }

        return blocks;
    }

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
            IsMuted: reader.GetBoolean(11));

        var counterparty = new ChatCounterparty(participant.Type.Counterparty(), Guid.Empty, null, null);

        if (await reader.NextResultAsync(cancellationToken)
            && await reader.ReadAsync(cancellationToken))
        {
            counterparty = new ChatCounterparty(
                ChatParticipantTypes.FromSqlValue(reader.GetString(0)),
                reader.GetGuid(1),
                Name: NullIfBlank(reader, 2),
                PhotoUrl: NullIfBlank(reader, 3));
        }

        return new ChatConversationDetail(conversation, me, counterparty);
    }

    private static ChatBlock ReadBlock(SqlDataReader reader, bool hasNameColumns) => new(
        ChatBlockId: reader.GetGuid(0),
        BlockerType: ChatParticipantTypes.FromSqlValue(reader.GetString(1)),
        BlockerId: reader.GetGuid(2),
        BlockedType: ChatParticipantTypes.FromSqlValue(reader.GetString(3)),
        BlockedId: reader.GetGuid(4),
        Reason: reader.IsDBNull(5) ? null : reader.GetString(5),
        BlockedName: hasNameColumns ? NullIfBlank(reader, 7) : null,
        BlockedPhotoUrl: hasNameColumns ? NullIfBlank(reader, 8) : null,
        CreatedAtUtc: ReadUtc(reader, 6) ?? default);

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
