# Prepared event rollout — not applied

The user requested that deployment remain prepared but unapplied. No Azure database, Cosmos document, API app, or Functions app has been changed by this rollout work.

## Read-only preflight results

The saved API connection targets `littersoft-dev` on `littersoftdb.database.windows.net`. The saved publish profiles target `littersoft-api-server`, `littersoft-petparent`, and `jobsweeper` in `LittersoftRG`.

The database contains 17 events. Seven still use retired category codes. The user approved these mappings in `EventCategoryMappings.json`:

| Current code | Approved replacement | Events |
| --- | --- | ---: |
| Charity | CharityAndVolunteering | 4 |
| ParentEducation | TalksAndLearning | 2 |
| SocialAndCultural | MeetupsAndSocials | 1 |

The remaining 7 AdoptionAndRescue and 3 HealthAndWellness events already use valid codes.

There are 21 active bookings whose email matches multiple accounts: 5 match two accounts, 5 match three, 1 matches four, and 10 match five. The database does not yet have `EventBookings.BookerAccountId`. An email-only backfill cannot safely determine their owners. The mapping template records only aggregate counts and its expected fields; it deliberately contains no names, emails, Firebase identifiers, or live account/booking IDs.

## Ready artifacts

- `DeployAll.sql`: the single database deployment file. Includes aggregate preflight checks, transactional schema upgrades, optional verified identity mappings, functions and every procedure. Run with `sqlcmd -I -b` during a maintenance window.
- `EventCategoryMappings.json`: the three approved category mappings, unapplied.
- `EventBookingIdentityMapping.template.json`: blank input shape for verified account mapping.
- `tests/Pawfront.EventLifecycle.Checks`: 42 integration/contract checks, previously passed.

The former event-only deployment was applied successfully to the disposable LocalDB database `PawfrontEventLifecycleChecks`. Its schema, procedures and rollout guards are now consolidated into `DeployAll.sql`; the separate preflight, schema, event deployment and generator files have been removed. `DeployAll.sql` refuses rollout while retired categories or unresolved active-booking identities remain. Execute it with `sqlcmd -I -b`; on any failure stop execution and roll back any open transaction in SSMS.

## Future rollout sequence

1. Reconfirm the intended environment. `DeployAll.sql` refreshes the aggregate preflight automatically; do not assume this snapshot is current.
2. Obtain verified `bookingId`, `accountId`, and `accountType` mappings for the 21 ambiguous bookings. Keep those mappings in an access-controlled operational location, outside source control.
3. Coordinate a maintenance window for both APIs and the sweep. Apply the category mappings to SQL and Cosmos together: physical event documents are partitioned by category, so updating SQL alone would lose their venue/capacity lookups. Copy and verify the new Cosmos documents before changing SQL references; retain the old copies until cutover verification succeeds.
4. Put verified identity updates into `#VerifiedEventBookingMappings` near the top of a private copy of `DeployAll.sql`, then run that one file. Its final identity guard must pass before commit. Do not bypass it or invent owners. This deploys the full database, including the other pending SQL changes in this workspace.
5. Deploy the APIs and Functions after the database upgrade. Existing publish profiles require usable Azure authentication; the configured Functions profile also requires Azure CLI, which was not available on PATH during preparation.
6. Verify authenticated endpoints, hosted/attendee lists, scheduler invocation, and real notification delivery. Local outbox assertions do not establish delivery to an actual device.

Publishing the current API/Functions projects would also publish their other uncommitted workspace changes. Review that release scope before using these profiles. No binaries or configuration containing production credentials have been exported as a release package.

When verified identities are supplied later, include them in the mapping table of a private copy of `DeployAll.sql`. It validates UUIDs, allowed account roles, duplicate booking IDs, existing owners, and matching accounts; the unresolved-identity guard still applies. Keep the private copy outside source control:

```sql
INSERT #VerifiedEventBookingMappings (BookingId, AccountId, AccountType)
VALUES ('<verified booking UUID>', '<verified account UUID>', N'PetParent');
```

The checked-in script contains no live identity assignments and remains unapplied.

Historical validation: the former generator's optional verified-mapping path was tested with synthetic UUIDs. LocalDB rejected nonexistent mappings with SQL error 51468 and rolled back the deployment transaction, as intended. No live identity data was exported.

Consolidation validation: `DeployAll.sql` passed SQL parsing, a fresh deployment and a rerun in a separate disposable LocalDB database. Synthetic checks confirmed invalid mappings and unresolved active-booking owners are rejected, all pending changes roll back, verified owner mappings apply, unique email matches backfill automatically, and retired categories stop later batches even without sqlcmd's `-b` option. No application database was changed.

