using Google.Api.Gax.Grpc;
using Google.Cloud.Firestore;
using Google.Cloud.Firestore.V1;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Pawfront.Application.Notifications;
using Pawfront.Infrastructure.Firebase;
using Timestamp = Google.Protobuf.WellKnownTypes.Timestamp;

internal static class AccountCleanupChecks
{
    public static async Task<int> RunAsync()
    {
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
        foreach (var audience in new[] { NotificationAudience.Provider, NotificationAudience.PetParent })
        {
            var account = Guid.NewGuid(); var newAccount = Guid.NewGuid();
            var job = new AccountNotificationCleanupJob(audience, account, "same-uid", DateTimeOffset.UtcNow);
            var client = new CleanupClient(); var db = FirestoreDb.Create(audience.ToString(), client);
            var user = $"projects/{db.ProjectId}/databases/(default)/documents/users/same-uid";
            for (var i = 0; i < 405; i++)
            {
                var doc = new Document { Name = user + "/notifications/" + i.ToString("D4"),
                    CreateTime = Timestamp.FromDateTimeOffset(job.DeletedAtUtc.AddMinutes(-1)), UpdateTime = Timestamp.FromDateTimeOffset(job.DeletedAtUtc.AddMinutes(-1)) };
                doc.Fields["data"] = new Value { MapValue = new MapValue { Fields = { ["recipientId"] = new Value { StringValue = account.ToString() } } } };
                client.Documents.Add(doc.Name, doc);
            }
            var legacy = new Document { Name = user + "/notifications/legacy", CreateTime = Timestamp.FromDateTimeOffset(job.DeletedAtUtc.AddDays(-1)), UpdateTime = Timestamp.FromDateTimeOffset(job.DeletedAtUtc.AddDays(-1)) };
            client.Documents.Add(legacy.Name, legacy);
            var fresh = legacy.Clone(); fresh.Name = user + "/notifications/new-profile";
            fresh.Fields["data"] = new Value { MapValue = new MapValue { Fields = { ["recipientId"] = new Value { StringValue = newAccount.ToString() } } } };
            client.Documents.Add(fresh.Name, fresh);
            await FirestoreNotificationDeleter.DeleteAsync(db, job, default);
            Check(client.Documents.Keys.Count(k => k.Contains("/notifications/")) == 1 && client.Documents.ContainsKey(fresh.Name), "Cleanup deletes all old-account and legacy notifications across pages, preserving a replacement account");
            Check(client.MarkerWrittenFirst && client.LargestDeleteBatch <= 400, "Deletion marker precedes bounded notification batches");
            Check(client.Documents.ContainsKey(user + "/deletedAccounts/" + account), "Deleted account marker is isolated to this account and audience");
            await FirestoreNotificationDeleter.DeleteAsync(db, job, default);
            Check(client.Documents.ContainsKey(fresh.Name), "Repeated cleanup is safe");
            var message = new PushMessage(audience, [], "Title", "Body", new Dictionary<string, string> { ["recipientId"] = account.ToString() }, null, "same-uid");
            await FirestoreNotificationWriter.CreateForActiveAccountAsync(db, message, "late", "BOOKING_REQUESTED", account, default);
            Check(!client.Documents.ContainsKey(user + "/notifications/late"), "Late worker cannot recreate deleted account notifications");
            await FirestoreNotificationWriter.CreateForActiveAccountAsync(db, message, "new", "BOOKING_REQUESTED", newAccount, default);
            Check(client.Documents.ContainsKey(user + "/notifications/new"), "A replacement account using the same UID can receive notifications");

            var store = new CleanupStore { Job = job }; var deleter = new Deleter { Fails = true };
            var cleanup = new AccountNotificationCleanup(store, deleter, NullLogger<AccountNotificationCleanup>.Instance);
            await cleanup.TryCleanupAsync(audience, account, default);
            Check(store.Failed == 1 && !store.Completed, "Remote failure remains queued instead of undoing account deletion");
            deleter.Fails = false;
            await cleanup.RetryPendingAsync(default);
            Check(store.Completed && deleter.Calls == 2, "Worker retries and completes failed cleanup");
        }
        return checks;
    }

    sealed class Deleter : INotificationInboxDeleter
    {
        public bool Fails; public int Calls;
        public Task DeleteAsync(AccountNotificationCleanupJob job, CancellationToken ct)
        { Calls++; return Fails ? Task.FromException(new InvalidOperationException("Offline")) : Task.CompletedTask; }
    }
    sealed class CleanupStore : IAccountNotificationCleanupStore
    {
        public AccountNotificationCleanupJob Job = null!; public bool Completed; public int Failed;
        public Task<AccountNotificationCleanupJob?> GetAsync(NotificationAudience audience, Guid id, CancellationToken ct) => Task.FromResult<AccountNotificationCleanupJob?>(Completed ? null : Job);
        public Task<IReadOnlyList<AccountNotificationCleanupJob>> ListDueAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<AccountNotificationCleanupJob>>([Job]);
        public Task CompleteAsync(AccountNotificationCleanupJob job, CancellationToken ct) { Completed = true; return Task.CompletedTask; }
        public Task FailAsync(AccountNotificationCleanupJob job, CancellationToken ct) { Failed++; return Task.CompletedTask; }
    }
    sealed class CleanupClient : FirestoreClient
    {
        public override FirestoreSettings Settings => FirestoreSettings.GetDefault();
        public Dictionary<string, Document> Documents = new(StringComparer.Ordinal);
        public bool MarkerWrittenFirst; public int LargestDeleteBatch;
        public override Task<BeginTransactionResponse> BeginTransactionAsync(BeginTransactionRequest request, CallSettings? settings = null)
            => Task.FromResult(new BeginTransactionResponse { Transaction = ByteString.CopyFromUtf8("test") });
        public override BatchGetDocumentsStream BatchGetDocuments(BatchGetDocumentsRequest request, CallSettings? settings = null)
            => new GetStream(request.Documents.Select(name => Documents.TryGetValue(name, out var doc)
                ? new BatchGetDocumentsResponse { Found = doc, ReadTime = Timestamp.FromDateTime(DateTime.UtcNow) }
                : new BatchGetDocumentsResponse { Missing = name, ReadTime = Timestamp.FromDateTime(DateTime.UtcNow) }).ToList());
        public override Task<CommitResponse> CommitAsync(CommitRequest request, CallSettings? settings = null)
        {
            var now = Timestamp.FromDateTime(DateTime.UtcNow); var response = new CommitResponse { CommitTime = now };
            LargestDeleteBatch = Math.Max(LargestDeleteBatch, request.Writes.Count(w => w.OperationCase == Google.Cloud.Firestore.V1.Write.OperationOneofCase.Delete));
            foreach (var write in request.Writes)
            {
                if (write.OperationCase == Google.Cloud.Firestore.V1.Write.OperationOneofCase.Delete)
                { if (!MarkerWrittenFirst) throw new Exception("Marker must precede deletes"); Documents.Remove(write.Delete); }
                else if (write.OperationCase == Google.Cloud.Firestore.V1.Write.OperationOneofCase.Update)
                { Documents[write.Update.Name] = write.Update.Clone(); if (write.Update.Name.Contains("/deletedAccounts/")) MarkerWrittenFirst = true; }
                response.WriteResults.Add(new Google.Cloud.Firestore.V1.WriteResult { UpdateTime = now });
            }
            return Task.FromResult(response);
        }
        public override RunQueryStream RunQuery(RunQueryRequest request, CallSettings? settings = null)
        {
            var start = request.StructuredQuery.StartAt?.Values.FirstOrDefault()?.ReferenceValue;
            var responses = Documents.Values.Where(d => d.Name.StartsWith(request.Parent + "/notifications/", StringComparison.Ordinal)
                    && (string.IsNullOrEmpty(start) || string.CompareOrdinal(d.Name, start) > 0))
                .OrderBy(d => d.Name, StringComparer.Ordinal).Take(request.StructuredQuery.Limit ?? 400)
                .Select(d => new RunQueryResponse { Document = d.Clone(), ReadTime = Timestamp.FromDateTime(DateTime.UtcNow) }).ToList();
            return new QueryStream(responses);
        }
    }
    sealed class GetStream(List<BatchGetDocumentsResponse> responses) : FirestoreClient.BatchGetDocumentsStream
    {
        public override AsyncServerStreamingCall<BatchGetDocumentsResponse> GrpcCall { get; } = new(new Reader<BatchGetDocumentsResponse>(responses), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
    }
    sealed class QueryStream(List<RunQueryResponse> responses) : FirestoreClient.RunQueryStream
    {
        public override AsyncServerStreamingCall<RunQueryResponse> GrpcCall { get; } = new(new Reader<RunQueryResponse>(responses), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
    }
    sealed class Reader<T>(List<T> responses) : IAsyncStreamReader<T>
    {
        private int index = -1;
        public T Current => responses[index];
        public Task<bool> MoveNext(CancellationToken ct) => Task.FromResult(++index < responses.Count);
    }
}
