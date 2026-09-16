-- Every review a pet parent has AUTHORED, keyed by the booking it belongs to.
--
-- Backs the `review` block on the parent's two "my bookings" lists
-- (GET /pet-parents/{id}/bookings and .../night-stay-bookings): one call per list
-- rather than a per-booking lookup, which is why it is scoped to the parent and
-- takes no booking ids. Both booking kinds come back together — the caller reads
-- one list at a time but the extra rows are a handful at most, and a single
-- procedure keeps the two surfaces from drifting.
--
-- [ReviewerType] = 'Parent' only: the provider's private rating OF this parent is
-- deliberately never returned on the parent host.
--
-- [BookingType] travels with [BookingId] because the two booking kinds live in
-- separate tables and share no id space, so the id alone cannot say which list
-- row a rating belongs to.
--
-- No THROW: an unknown parent, or one who has reviewed nothing, is an empty set —
-- the ordinary case, not an error.
CREATE OR ALTER PROCEDURE [Review].[ListPetParentBookingReviews]
    @PetParentId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [BookingType],
           [BookingId],
           [Rating]
    FROM [Review].[BookingReviews]
    WHERE [PetParentId] = @PetParentId
      AND [ReviewerType] = N'Parent';
END;
