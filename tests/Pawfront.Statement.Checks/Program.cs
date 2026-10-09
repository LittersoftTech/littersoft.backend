using Pawfront.Application.Earnings;
using Pawfront.Api.Statements;

void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

foreach (var status in new[] { "PARENT_NO_SHOW", "PROVIDER_NO_SHOW", "PARENT_CANCELLED", "PROVIDER_CANCELLED", "PROVIDER_DECLINED", "JOB_EXPIRED", "OTP_MAX_ATTEMPTS_EXCEEDED" })
    Check(ProviderStatement.Group(status) == "Unpaid", status);
foreach (var status in new[] { "CONFIRMED", "START_JOB", "IN_PROGRESS", "JOB_STARTED", "ENDING", "COMPLETED", "PROVIDER_ACCEPTED_MODIFICATION", "MODIFICATION_REQUEST_BY_PARENT" })
    Check(ProviderStatement.Group(status) == "Pending", status);
Check(ProviderStatement.Group("PAID") == "Received", "Paid");
Check(ProviderStatement.Group("EXPIRED") == "Ignored", "Expired only");
Check(ProviderStatement.Group("CREATED") is null, "Unaccepted requests excluded");
Check(ProviderStatement.Group("APPROVAL_NEEDED") is null, "Legacy unaccepted excluded");

var start = new DateOnly(2026, 9, 1);
Check(ProviderStatement.ValidateRange(start, start) is null, "Same-day range allowed");
Check(ProviderStatement.ValidateRange(start, start.AddDays(29)) is null, "30 inclusive days allowed");
Check(ProviderStatement.ValidateRange(start, start.AddDays(30)) is not null, "31 inclusive days rejected");
Check(ProviderStatement.ValidateRange(start, start.AddDays(-1)) is not null, "Reversed range rejected");
Check(ProviderStatement.ValidateRange(null, start) is not null, "Missing from rejected");
Check(ProviderStatement.ValidateRange(start, null) is not null, "Missing to rejected");
Check(ProviderStatement.ValidateRange(null, null) is not null, "Open-ended export rejected");
Check(ProviderStatement.ValidateRange(new DateOnly(2028, 2, 1), new DateOnly(2028, 3, 1)) is null, "Leap-year 30-day range allowed");

var row = new ProviderEarningsBookingRow("SingleDay", Guid.NewGuid(), "PF-000123", null,
    "PENDING", "PAID", "PetSitter", "DayCare", null, new DateOnly(2026, 9, 1),
    null, null, null, null, null, "Alex Example", "Milo", true, false, true,
    100m, 10m, null, "CASH");
Check(ProviderStatement.Fee(row) == 0 && ProviderStatement.Net(row) == 100, "Cash net=gross");
Check(ProviderStatement.Net(row with { PaymentMethod = "Card" }) == 90, "Digital fee retained");
Check(ProviderStatement.Net(row with { IsPrivate = true, PaymentMethod = null }) == 100, "Private net=gross");
Check(ProviderStatement.Net(row with { Status = "JOB_EXPIRED", PaymentMethod = null }) == 100, "No fee on failed jobs");
Check(ProviderStatement.Net(row with { GrossAmount = null }) is null, "Unpriced stays unknown");
var rows = Enumerable.Range(0, 85).Select(i => row with {
    JobId = $"PF-{i:000000}", Status = new[] { "PAID", "CONFIRMED", "JOB_EXPIRED", "EXPIRED" }[i % 4],
    IsPrivate = i % 5 == 0, CustomerName = i % 7 == 0 ? "Customer with a very long name to verify wrapping" : "Alex Example"
}).ToArray();
var pdf = ProviderStatementPdf.Render(Guid.Empty, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), rows);
Check(System.Text.Encoding.ASCII.GetString(pdf, 0, 5) == "%PDF-", "PDF signature");
Check(ProviderStatementPdf.Render(Guid.Empty, null, null, []).Length > 0, "Empty statement");
if (args.Length > 0) File.WriteAllBytes(args[0], pdf);
Console.WriteLine("Statement grouping, cash/private fees, unpriced rows, empty and multi-page PDF checks passed.");
