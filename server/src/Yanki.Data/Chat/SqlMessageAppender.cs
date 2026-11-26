using System.Data;
using Microsoft.Data.SqlClient;
using Yanki.Core.Data;

namespace Yanki.Data.Chat;

/// <summary>
/// The message write hot path, as one parameterized batch and one short transaction.
/// </summary>
/// <remarks>
/// <para>
/// Sequence allocation is <c>UPDATE conversations SET last_seq = last_seq + 1 OUTPUT
/// inserted.last_seq</c>. The exclusive row lock on the conversation row is held for the few
/// microseconds between the UPDATE and the COMMIT, and it is what makes the order total: concurrent
/// senders queue behind it, each gets the next integer, and a rollback gives the number back, so the
/// committed sequence is gapless. No sequence object, no identity, no cluster coordinator, and the
/// value is known before the insert so it can be echoed to the sender immediately.
/// </para>
/// <para>
/// Idempotency is the unique index on <c>(sender_id, client_msg_id)</c>: a retried send finds the
/// existing row (fast path, no transaction) or loses the insert race and re-reads it (catch path).
/// The key is global per sender, so a row found in another conversation is a conflict, never a
/// duplicate.
/// </para>
/// <para>
/// <c>SET XACT_ABORT ON</c> makes a client timeout or cancellation (an attention, which TRY/CATCH
/// cannot catch) roll the transaction back on the server, so the conversation row lock is never left
/// behind on a pooled connection.
/// </para>
/// </remarks>
public sealed class SqlMessageAppender(SqlConnectionSource connections, TimeProvider timeProvider) : IMessageAppender
{
    /// <summary>Raised by <c>THROW</c> in the batch when the conversation row does not exist.</summary>
    private const int ConversationNotFoundError = 50404;

    /// <summary>
    /// Raised by <c>THROW</c> in the batch when the sender already used the client message id in a
    /// different conversation.
    /// </summary>
    private const int ClientMsgIdConflictError = 50409;

    private const string AppendSql = """
        SET XACT_ABORT ON;
        SET NOCOUNT ON;

        DECLARE @id uniqueidentifier, @seq bigint, @created datetime2(3), @dedup bit = 0;
        DECLARE @existing_conversation uniqueidentifier;
        DECLARE @alloc TABLE (seq bigint NOT NULL);

        SELECT @id = m.id, @seq = m.seq, @created = m.created_at, @existing_conversation = m.conversation_id
        FROM dbo.messages AS m
        WHERE m.sender_id = @sender_id AND m.client_msg_id = @client_msg_id;

        IF @id IS NOT NULL
        BEGIN
            IF @existing_conversation <> @conversation_id
            BEGIN
                THROW 50409, 'yanki: client message id already used in another conversation', 1;
            END;

            SET @dedup = 1;
        END
        ELSE
        BEGIN
            BEGIN TRY
                BEGIN TRANSACTION;

                UPDATE dbo.conversations
                   SET last_seq = last_seq + 1,
                       last_message_at = @created_at,
                       updated_at = @created_at
                OUTPUT inserted.last_seq INTO @alloc (seq)
                 WHERE id = @conversation_id;

                SELECT @seq = seq FROM @alloc;

                IF @seq IS NULL
                BEGIN
                    ROLLBACK TRANSACTION;
                    THROW 50404, 'yanki: conversation does not exist', 1;
                END;

                INSERT INTO dbo.messages
                    (conversation_id, seq, id, sender_id, kind, parent_id, body, metadata,
                     client_msg_id, edited_at, deleted_at, created_at)
                VALUES
                    (@conversation_id, @seq, @new_id, @sender_id, @kind, @parent_id, @body, @metadata,
                     @client_msg_id, NULL, NULL, @created_at);

                COMMIT TRANSACTION;

                SET @id = @new_id;
                SET @created = @created_at;
            END TRY
            BEGIN CATCH
                IF XACT_STATE() <> 0
                BEGIN
                    ROLLBACK TRANSACTION;
                END;

                IF ERROR_NUMBER() IN (2601, 2627)
                BEGIN
                    SELECT @id = m.id, @seq = m.seq, @created = m.created_at,
                           @existing_conversation = m.conversation_id
                    FROM dbo.messages AS m
                    WHERE m.sender_id = @sender_id AND m.client_msg_id = @client_msg_id;

                    IF @id IS NULL
                    BEGIN
                        THROW;
                    END;

                    IF @existing_conversation <> @conversation_id
                    BEGIN
                        THROW 50409, 'yanki: client message id already used in another conversation', 1;
                    END;

                    SET @dedup = 1;
                END
                ELSE
                BEGIN
                    THROW;
                END;
            END CATCH;
        END;

        SELECT @id AS id, @seq AS seq, @created AS created_at, @dedup AS dedup;
        """;

    /// <inheritdoc />
    public async Task<AppendedMessage> AppendAsync(MessageAppendRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var createdAt = SqlTime.UtcNow(timeProvider);

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connections.NewCommand(AppendSql, connection);

        command.Parameters.Add("@conversation_id", SqlDbType.UniqueIdentifier).Value = request.ConversationId;
        command.Parameters.Add("@sender_id", SqlDbType.UniqueIdentifier).Value = request.SenderId;
        command.Parameters.Add("@client_msg_id", SqlDbType.UniqueIdentifier).Value = request.ClientMsgId;
        command.Parameters.Add("@new_id", SqlDbType.UniqueIdentifier).Value = Guid.CreateVersion7();
        command.Parameters.Add("@kind", SqlDbType.TinyInt).Value = (byte)request.Kind;
        command.Parameters.Add("@parent_id", SqlDbType.UniqueIdentifier).Value =
            request.ParentId.HasValue ? request.ParentId.Value : DBNull.Value;
        command.Parameters.Add("@body", SqlDbType.NVarChar, -1).Value = (object?)request.Body ?? DBNull.Value;
        command.Parameters.Add("@metadata", SqlDbType.NVarChar, -1).Value = (object?)request.Metadata ?? DBNull.Value;
        SqlTime.AddTimestamp(command, "@created_at", createdAt);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(
                CommandBehavior.SingleRow | CommandBehavior.SingleResult,
                cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("The message append batch returned no row.");
            }

            return new AppendedMessage(
                reader.GetGuid(0),
                reader.GetInt64(1),
                DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc),
                reader.GetBoolean(3));
        }
        catch (SqlException ex) when (ex.Number == ConversationNotFoundError)
        {
            throw new InvalidOperationException(
                $"Conversation {request.ConversationId} does not exist, so no message could be appended.",
                ex);
        }
        catch (SqlException ex) when (ex.Number == ClientMsgIdConflictError)
        {
            throw new ClientMsgIdConflictException(
                $"Client message id {request.ClientMsgId} was already used by this sender in another conversation.",
                ex);
        }
    }
}
