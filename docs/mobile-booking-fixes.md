# Mobile booking and profile updates

Night stays use UTC calendar dates for creation, modification requests, acceptance and expiry. Check-in today is allowed regardless of drop-off time; past check-in dates and checkout on or before check-in are refused. Pending requests and modification proposals still have their existing 24-hour timeout. Single-day services retain their two-hour lead-time rule.

Pet-parent onboarding requires completed basic information, pets, pet medical information, identity and verified email. Mobile verification is informational. Changing the mobile number still clears its verified flag. Provider onboarding and OTP requirements are unchanged.

Both booking detail responses include nullable `paymentDetails.cashNotReceivedAtUtc`. Booking list rows include `cashNotReceivedAtUtc` on their booking object; history, earnings, shared parent/provider history and the provider job list also include it. The latest provider report wins and recording payment clears it. Provider booking tickets with category or reason `Cash Not Received` also record the timestamp. Existing `PAYMENT_NOT_RECEIVED` status behavior and `BOOKING_CASH_NOT_RECEIVED` notifications are preserved.

The six Report User reason values apply only to `report-chat`, on both hosts. Booking tickets accept optional free-form category and reason, with a required comment. Event and app-issue reason handling is unchanged.

Parents can send `{ "cancelBooking": true }` to either single-day or night-stay `modifications/decline` route. The operation atomically discards the provider proposal and cancels the booking, returns `PARENT_CANCELLED` with `cancellationReason: "CancelledAfterModificationDeclined"`, and notifies the provider of cancellation. This reason is excluded from service reliability penalties. Omitting the flag retains the ordinary decline behavior. Manual cancellation still uses its existing reliability rules.

Pet galleries accept at most 12 photos. Attempting to add a thirteenth returns `409 PetPhotoLimitReached`. Deleting a gallery photo frees one slot. The cap is enforced under a database lock, including concurrent uploads.

Deploy the updated `database/Pawfront.Database/Deployment/DeployAll.sql` before deploying either API. It adds nullable fields to existing booking tables and updates the affected procedures and reliability triggers. Regression SQL in `tests/booking-mobile-regressions.sql` is intended only for a disposable database after this deployment.
