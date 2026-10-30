# Provider earnings statement

`GET /providers/{providerId}/earnings/statement?from=2026-09-01&to=2026-09-30`

Returns `application/pdf` with an attachment filename. Requires the authenticated provider to own the route ID (otherwise 403). Both `from` and `to` are required ISO dates. The range may span at most 30 calendar days, counting both endpoints (for example, September 1-30 is valid). Missing dates, reversed bounds, or ranges exceeding 30 days return 400 (`InvalidRequest`). Dates refer to service date (checkout date for night stays), not payment date. Empty ranges produce a valid PDF with zero totals.

The export includes all matching single-day and night-stay jobs, including private jobs, without the screen's pagination limit:

| Section | Statuses |
| --- | --- |
| Received | PAID |
| Pending | CONFIRMED and confirmed-equivalent modification states, modification requests, START_JOB, IN_PROGRESS, legacy JOB_STARTED/ENDING, COMPLETED awaiting payment |
| Unpaid | Parent/provider no-show, parent/provider cancellation, provider declined, JOB_EXPIRED, OTP_MAX_ATTEMPTS_EXCEEDED |
| Ignored | EXPIRED only |

Unaccepted CREATED/APPROVAL_NEEDED requests are excluded. Each section has job counts and gross, fee, and net totals. Cash and private jobs have zero deducted fee and net equals gross. Unpaid/ignored values are potential amounts, not received earnings, and carry no fee. Unpriced jobs display `-` and are explicitly counted outside monetary totals. Other payment methods retain the earnings store's fee. These export rules do not alter payment ledgers or booking invoices.

Verification: `dotnet run --project tests/Pawfront.Statement.Checks`
