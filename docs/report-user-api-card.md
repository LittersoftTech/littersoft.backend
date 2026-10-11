# Report a User API card

The provider and pet-parent apps use the same six backend reason values for booking and chat reports.

## Reasons

`GET /providers/{providerId}/support-tickets/report-user-reasons`

`GET /pet-parents/{petParentId}/support-tickets/report-user-reasons`

The response `data` is an array of these exact strings, in display order:

1. `Offensive or threatening messages`
2. `Unwanted advertising`
3. `Fraud or fake profile`
4. `Asking to book outside Pawfront`
5. `Concern for an animal's welfare`
6. `Something else`

## Submit a report

Use `POST .../support-tickets/report-incident` for a booking or `POST .../support-tickets/report-chat` for a conversation. Set `reason` to one of the strings above. For `Something else`, include a nonempty `comment` from the description box. For the other five reasons, `comment` is optional. Unknown or missing reasons return `400 InvalidRequest`.

Booking reports can also use `POST .../support-tickets/report-incident-with-photos` with the same `reason` and `comment` form fields.

Each side may have its own open ticket on the same booking or conversation. A second open ticket by the *same raiser* returns `409 TicketAlreadyOpen`. List and detail endpoints return only tickets raised by the caller.
