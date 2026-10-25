# Services booking changes

Private jobs use `JOB_INCOMPLETE` (display: **Incomplete Job**) when a started job remains unfinished after the provider working-day cutoff. The existing scheduled `SettleUnstartedJobsAsNoShow` sweep performs this transition and records status history. Its cutoff is the later of the provider closing time and booking end; missing/closed availability falls back to the end of the booking day. Completed jobs are excluded.

Booking detail and list responses include `providerModificationUsed`. Hide Modify Job when true. The flag becomes true only when the parent accepts a provider proposal. Declined or expired proposals do not count. Each party has one accepted change of their own; detail responses expose `isParentModificationDone` and `isProviderModificationDone` independently. History supplies these values for existing bookings without a backfill. See [email status and booking limits](email-status-and-booking-modification-limits.md).

A repeat provider submission returns HTTP **409**, code **ModificationLimitReached**. The database enforces this while holding the booking row lock, including simultaneous submissions. Both single-day and night-stay booking endpoints use the rule.

Deploy the updated stored procedures with `database/Pawfront.Database/Deployment/DeployAll.sql` together with both APIs. The scheduled sweeper must run for automatic status changes. SQL regression checks are in `tests/booking-mobile-regressions.sql`; run only against a disposable database.
