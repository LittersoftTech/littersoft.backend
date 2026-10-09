# Invoice generation and recovery

Payment commits invoice rows before publishing generation work. Queue-triggered rendering remains the normal path, with a maximum idle polling interval of two seconds (cold starts and rendering can add latency).

The recovery timer runs every 30 seconds. It reads due work older than one minute from SQL and renders directly, so a publishing failure or unavailable queue listener does not also break recovery. Existing SQL leases, attempt limits, and retry backoff still apply. A newly lost message becomes eligible after one minute and is normally picked up by the next timer tick; this is not a delivery SLA. Recovery requires the Functions host, SQL and blob storage to be healthy.

Both download APIs preserve their existing PDF success and 409 pending responses. After the ownership-scoped lookup, downloading a Pending invoice republishes its booking request. Generating and Failed invoices are not requeued by downloads. Pending/Generating responses include `Retry-After: 2`; clients should wait before requesting again. Terminal failures return `InvoiceGenerationFailed` without a retry hint. Clients should stop automatic polling on this code. This is polling support, not a WebSocket subscription.

Deploy both API hosts and Pawfront.Functions, including host.json. No database migration is required for these changes: the worker passes the shorter grace period to the existing procedure. Keep the queue name and InvoiceStorage connection configured in Function App settings; worker appsettings.json alone cannot configure a host trigger.

For an existing failure, inspect Billing.Invoices.Status, AttemptCount, LastError and NextAttemptAtUtc and correlate Functions logs by booking ID. Failed rows at the attempt cap require diagnosis and controlled operator recovery; these changes do not silently reset them. Production logs and storage configuration were not available during local validation, so the production root cause is not confirmed.

Run `dotnet run --project tests/Pawfront.Invoice.Checks` for real PDF rendering of parent/provider, single-day/night-stay and long-identity cases, plus download lifecycle checks.
