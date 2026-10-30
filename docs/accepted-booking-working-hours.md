# Accepted booking working hours

Confirmation stores `WorkingHoursOpensAtUtc` and `WorkingHoursClosesAtUtc` on the
booking. Single-day jobs use the booked date and start; night stays use check-in
and drop-off. These UTC instants preserve the provider's local working day,
including timezone and daylight-saving conversion. Breaks do not restrict an
accepted job. Providers with no saved schedule retain a full UTC day.

Changing weekly availability preserves those snapshots. A mutually accepted
reschedule captures the newly agreed day's hours; declining a proposal preserves
the original hours. New requests and reschedules still validate current availability.

Start Job and Wait for Customer are available from the saved opening instant
through the saved closing instant on the booked day. Single-day reminders and the automatic no-show /
incomplete-job cutoff use the snapshot too. The sweep still uses the later of
the booked end and closing. Manual no-show grace periods and completion's existing
status checks remain in force; completion does not read weekly availability.

Deploy `database/Pawfront.Database/Deployment/DeployAll.sql` before the APIs.
It adds the nullable columns and backfills accepted legacy rows using the schedule
available at deployment. Availability saves also capture uncaptured accepted rows
before replacing the old schedule. Previously overwritten historical hours cannot
be reconstructed from the weekly table; those affected bookings need verified
historical values if their schedule had already changed before deployment.

Run `dotnet run --project tests/Pawfront.ServicesModule.Checks -- --sql` against
the disposable `PawfrontServicesModuleChecks` database for acceptance, shortened
hours, closed weekdays, waiting, start, completion and sweep regression checks.
