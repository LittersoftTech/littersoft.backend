-- Services V4 card identity. Keep stable notification types and select by audience/reason.
CREATE OR ALTER FUNCTION [Notification].[ServiceNotificationCode]
(
    @Type NVARCHAR(64), @Audience NVARCHAR(16), @AbsentParty NVARCHAR(32),
    @NoShowReason NVARCHAR(32), @CompletionScenario NVARCHAR(32)
)
RETURNS NVARCHAR(16)
AS
BEGIN
    RETURN CASE
        WHEN @Type=N'BOOKING_COMPLETED' THEN CASE WHEN @CompletionScenario=N'HandOver' THEN N'P-U12' ELSE N'P-U11' END
        WHEN @Type=N'BOOKING_NO_SHOW_REPORTED' AND @Audience=N'PetParent' THEN CASE WHEN @NoShowReason=N'OtpNotShared' THEN N'P-U10' ELSE N'P-U9' END
        WHEN @Type=N'BOOKING_NO_SHOW_AUTO_SETTLED' THEN CASE WHEN @AbsentParty=N'the customer'
            THEN CASE WHEN @Audience=N'Provider' THEN N'V-S11' ELSE N'P-S12' END
            ELSE CASE WHEN @Audience=N'Provider' THEN N'V-S9' ELSE N'P-S8' END END
        WHEN @Type=N'BOOKING_ACCEPTED' AND @Audience=N'PetParent' THEN N'P-U1'
        WHEN @Type=N'BOOKING_DECLINED' AND @Audience=N'PetParent' THEN N'P-U2'
        WHEN @Type=N'BOOKING_CANCELLED_BY_PROVIDER' AND @Audience=N'PetParent' THEN N'P-U3'
        WHEN @Type=N'BOOKING_EXPIRED_FOR_PARENT' AND @Audience=N'PetParent' THEN N'P-S1'
        WHEN @Type=N'BOOKING_START_OTP_ISSUED' AND @Audience=N'PetParent' THEN N'P-U7'
        WHEN @Type=N'BOOKING_IN_PROGRESS' AND @Audience=N'PetParent' THEN N'P-U8'
        WHEN @Type=N'BOOKING_PAID' AND @Audience=N'PetParent' THEN N'P-U13'
        WHEN @Type=N'BOOKING_CASH_NOT_RECEIVED' AND @Audience=N'PetParent' THEN N'P-U15'
        WHEN @Type=N'BOOKING_MODIFICATION_REQUESTED_BY_PROVIDER' AND @Audience=N'PetParent' THEN N'P-U4'
        WHEN @Type=N'BOOKING_MODIFICATION_ACCEPTED_BY_PROVIDER' AND @Audience=N'PetParent' THEN N'P-U5'
        WHEN @Type=N'BOOKING_MODIFICATION_DECLINED_BY_PROVIDER' AND @Audience=N'PetParent' THEN N'P-U6'
        WHEN @Type=N'BOOKING_OTP_ATTEMPTS_EXCEEDED' AND @Audience=N'PetParent' THEN N'P-S11'
        WHEN @Type=N'BOOKING_START_OTP_NOT_SEEN' AND @Audience=N'PetParent' THEN N'P-S9'
        WHEN @Type=N'BOOKING_START_OTP_NOT_SHARED' AND @Audience=N'PetParent' THEN N'P-S10'
        WHEN @Type=N'BOOKING_START_OTP_NOT_ENTERED' AND @Audience=N'Provider' THEN N'V-S10'
        WHEN @Type=N'BOOKING_COMPLETION_TIME_PASSED' AND @Audience=N'PetParent' THEN N'P-S13'
        WHEN @Type=N'BOOKING_PICKUP_OVERDUE' AND @Audience=N'PetParent' THEN N'P-S14'
        WHEN @Type=N'BOOKING_NOT_MARKED_COMPLETE' AND @Audience=N'Provider' THEN N'V-S12'
        WHEN @Type=N'BOOKING_REQUESTED' AND @Audience=N'Provider' THEN N'V-S1'
        WHEN @Type=N'NIGHT_STAY_BOOKING_REQUESTED' AND @Audience=N'Provider' THEN N'V-S1'
        WHEN @Type=N'BOOKING_CANCELLED_BY_PARENT' AND @Audience=N'Provider' THEN N'V-U1'
        WHEN @Type=N'BOOKING_EXPIRED_FOR_PROVIDER' AND @Audience=N'Provider' THEN N'V-S2'
        WHEN @Type=N'BOOKING_MODIFICATION_REQUESTED_BY_PARENT' AND @Audience=N'Provider' THEN N'V-U2'
        WHEN @Type=N'BOOKING_MODIFICATION_ACCEPTED_BY_PARENT' AND @Audience=N'Provider' THEN N'V-U3'
        WHEN @Type=N'BOOKING_MODIFICATION_DECLINED_BY_PARENT' AND @Audience=N'Provider' THEN N'V-U4'
        WHEN @Type=N'BOOKING_MODIFICATION_TIMED_OUT' AND @Audience=N'PetParent' THEN N'P-S2'
        WHEN @Type=N'BOOKING_MODIFICATION_EXPIRED' AND @Audience=N'PetParent' THEN N'P-S2'
        WHEN @Type=N'BOOKING_MODIFICATION_TIMED_OUT' AND @Audience=N'Provider' THEN N'V-S3'
        WHEN @Type=N'BOOKING_MODIFICATION_EXPIRED' AND @Audience=N'Provider' THEN N'V-S3'
        WHEN @Type=N'BOOKING_REMINDER_DAY_BEFORE' AND @Audience=N'PetParent' THEN N'P-S4'
        WHEN @Type=N'BOOKING_REMINDER_DAY_BEFORE' AND @Audience=N'Provider' THEN N'V-S5'
        WHEN @Type=N'BOOKING_REMINDER_STARTING_SOON' AND @Audience=N'PetParent' THEN N'P-S5'
        WHEN @Type=N'BOOKING_REMINDER_STARTING_SOON' AND @Audience=N'Provider' THEN N'V-S6'
        WHEN @Type=N'BOOKING_NOT_STARTED_HALFWAY' AND @Audience=N'PetParent' THEN N'P-S6'
        WHEN @Type=N'BOOKING_NOT_STARTED_HALFWAY' AND @Audience=N'Provider' THEN N'V-S7'
        WHEN @Type=N'BOOKING_NOT_STARTED_WINDOW_ENDED' AND @Audience=N'PetParent' THEN N'P-S7'
        WHEN @Type=N'BOOKING_NOT_STARTED_WINDOW_ENDED' AND @Audience=N'Provider' THEN N'V-S8'
        WHEN @Type=N'MESSAGE_RECEIVED' AND @Audience=N'PetParent' THEN N'P-U14'
        WHEN @Type=N'MESSAGE_RECEIVED' AND @Audience=N'Provider' THEN N'V-U5'
        WHEN @Type=N'INVOICE_ISSUED' AND @Audience=N'PetParent' THEN N'P-S16'
        WHEN @Type=N'INVOICE_ISSUED' AND @Audience=N'Provider' THEN N'V-S14'
        WHEN @Type=N'DISPUTE_RESOLVED' AND @Audience=N'PetParent' THEN N'P-S15'
        WHEN @Type=N'DISPUTE_RESOLVED' AND @Audience=N'Provider' THEN N'V-S13'
        WHEN @Type=N'BOOKING_COMPLETION_OVERDUE' AND @Audience=N'Provider' THEN N'V-S15'
        ELSE NULL END;
END;
