using Google.Api.Gax.Grpc;
using Google.Cloud.Firestore;
using Google.Cloud.Firestore.V1;
using Grpc.Core;
using Pawfront.Application.Notifications;
using Pawfront.Infrastructure.Firebase;
using Timestamp = Google.Protobuf.WellKnownTypes.Timestamp;

internal static class FirestoreReadChecks
{
    public static async Task<int> RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            checks++;
        }
        foreach (var audience in new[] { NotificationAudience.Provider, NotificationAudience.PetParent })
        {
            var client = new QueryClient();
            var db = FirestoreDb.Create(audience.ToString().ToLowerInvariant(), client);
            var timestamp = Timestamp.FromDateTime(new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc).AddTicks(1234560));
            var prefix = $"projects/{db.ProjectId}/databases/(default)/documents/users/user-a";
            foreach (var id in new[] { "c", "b", "a" })
            {
                var document = new Document { Name = prefix + "/notifications/" + id, CreateTime = timestamp, UpdateTime = timestamp };
                document.Fields.Add("title", new Value { StringValue = "Title" });
                document.Fields.Add("body", new Value { StringValue = "Body" });
                document.Fields.Add("type", new Value { StringValue = "MESSAGE_RECEIVED" });
                document.Fields.Add("createdAt", new Value { TimestampValue = timestamp });
                document.Fields.Add("isRead", new Value { BooleanValue = id == "c" });
                var data = new MapValue();
                data.Fields.Add("conversationId", new Value { StringValue = "chat-1" });
                document.Fields.Add("data", new Value { MapValue = data });
                client.Responses.Add(new RunQueryResponse { Document = document, ReadTime = timestamp });
            }
            var page = await FirestoreNotificationReader.ReadPageAsync(db, audience, "user-a", 2, null, default);
            Check(client.Request!.Parent == prefix && client.Request.StructuredQuery.From.Single().CollectionId == "notifications", "Query restricted to user's own collection");
            Check(client.Request.StructuredQuery.Limit == 3 && client.Request.StructuredQuery.OrderBy.Count == 2
                && client.Request.StructuredQuery.OrderBy[0].Field.FieldPath == "createdAt"
                && client.Request.StructuredQuery.OrderBy[1].Field.FieldPath == "__name__"
                && client.Request.StructuredQuery.OrderBy.All(x => x.Direction == StructuredQuery.Types.Direction.Descending),
                "Newest-first query with stable tie-breaker and lookahead");
            Check(page.Items.Count == 2 && page.Items[0].IsRead && page.Items[0].Data["conversationId"] == "chat-1", "Stored read state and routing data returned");
            var cursor = FirestoreNotificationReader.DecodeCursor(page.NextCursor, audience, "user-a")!;
            Check(cursor.Id == "b" && cursor.Ticks == timestamp.ToDateTime().Ticks, "Cursor uses last returned item with full Firestore precision");
            client.Responses.RemoveRange(0, 2);
            var next = await FirestoreNotificationReader.ReadPageAsync(db, audience, "user-a", 2, cursor, default);
            Check(!client.Request.StructuredQuery.StartAt.Before
                && client.Request.StructuredQuery.StartAt.Values[0].TimestampValue == timestamp
                && client.Request.StructuredQuery.StartAt.Values[1].ReferenceValue == prefix + "/notifications/b",
                "Next page starts strictly after timestamp and document ID");
            Check(next.Items.Single().NotificationId == "a" && next.NextCursor is null, "Last page has no cursor");
            Check(await FirestoreNotificationReadStateStore.MarkReadAsync(db, "user-a", "a", default), "Firestore single read succeeds");
            var write = client.LastCommit!.Writes.Single();
            Check(write.Update.Name == prefix + "/notifications/a" && write.UpdateMask.FieldPaths.SequenceEqual(new[] { "isRead" })
                && write.Update.Fields["isRead"].BooleanValue && write.CurrentDocument.Exists, "Single update is scoped, requires existing document, and changes only read state");
            var readBack = await FirestoreNotificationReader.ReadPageAsync(db, audience, "user-a", 2, null, default);
            Check(readBack.Items.Single().IsRead, "Firestore GET observes updated read state");
            client.Responses[0].Document.Fields["isRead"].BooleanValue = false;
            await FirestoreNotificationReadStateStore.MarkAllReadAsync(db, "user-a", default);
            Check(client.LastCommit.Writes.Single().Update.Fields["isRead"].BooleanValue
                && client.Request.Parent == prefix && client.Request.StructuredQuery.Limit == 400,
                "Read all uses bounded scoped query and read-state update");
            client.Responses.Clear();
            client.Responses.Add(new RunQueryResponse { ReadTime = timestamp });
            var empty = await FirestoreNotificationReader.ReadPageAsync(db, audience, "user-a", 2, null, default);
            Check(empty.Items.Count == 0 && empty.NextCursor is null, "Empty Firestore collection");
        }
        return checks;
    }

    private sealed class QueryClient : FirestoreClient
    {
        public RunQueryRequest? Request;
        public List<RunQueryResponse> Responses = [];
        public CommitRequest? LastCommit;
        public override Task<CommitResponse> CommitAsync(CommitRequest request, CallSettings? callSettings = null)
        {
            LastCommit = request;
            var now = Timestamp.FromDateTime(DateTime.UtcNow);
            var result = new CommitResponse { CommitTime = now };
            foreach (var write in request.Writes)
            {
                var document = Responses.Select(x => x.Document).FirstOrDefault(x => x?.Name == write.Update.Name);
                if (document is not null) document.Fields["isRead"] = write.Update.Fields["isRead"].Clone();
                result.WriteResults.Add(new Google.Cloud.Firestore.V1.WriteResult { UpdateTime = now });
            }
            return Task.FromResult(result);
        }
        public override RunQueryStream RunQuery(RunQueryRequest request, CallSettings? callSettings = null)
        {
            Request = request;
            return new QueryStream(Responses);
        }
    }
    private sealed class QueryStream(List<RunQueryResponse> responses) : FirestoreClient.RunQueryStream
    {
        public override AsyncServerStreamingCall<RunQueryResponse> GrpcCall { get; } = new(
            new ResponseReader(responses), Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess, () => new Metadata(), () => { });
    }
    private sealed class ResponseReader(List<RunQueryResponse> responses) : IAsyncStreamReader<RunQueryResponse>
    {
        private int index = -1;
        public RunQueryResponse Current => responses[index];
        public Task<bool> MoveNext(CancellationToken cancellationToken) => Task.FromResult(++index < responses.Count);
    }
}
