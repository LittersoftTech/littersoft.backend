using Pawfront.Application.Notifications;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Pawfront.Functions.Invoices;

/// <summary>
/// The PET PARENT's invoice: issued by the SERVICE PROVIDER to the pet parent for
/// ONE booked service. One invoice = one service the customer received from that
/// provider — never a period roll-up and never several services on one document.
/// Pawfront generates and delivers it on the provider's behalf, and the notes say
/// so, because the document is not Pawfront's.
///
/// The Pawfront fee is NOT broken out to the customer. They see the service line
/// and one total. In this system the fee is carved OUT of that total (gross is
/// what the parent pays; provider net = gross - fee), which is why the subtotal
/// and the total are the same figure and the page reconciles — the supplied HTML
/// template's own comment flagged that it did not, because it modelled the fee as
/// being added on top.
///
/// Deliberately absent: any MWST / UID identifier for either party. No such column
/// exists anywhere in the schema, and printing an invented tax identifier on a
/// financial document would be worse than omitting it. The note that any provider
/// MWST is already inside the amount is kept, since that remains true.
/// </summary>
internal sealed class ParentInvoiceDocument(
    InvoiceBookingFacts facts,
    ClaimedInvoice invoice,
    InvoiceProviderIdentity provider) : IDocument
{
    public DocumentMetadata GetMetadata() => new()
    {
        Title = $"Pawfront_Invoice_{invoice.InvoiceNumber}",
        Author = provider.BusinessName ?? ProviderPersonName(),
        Subject = "Invoice for pet care services"
    };

    public void Compose(IDocumentContainer document)
    {
        document.Page(page =>
        {
            page.Size(PageSizes.A4);
            // The letterhead band bleeds to the paper edge, so the page carries no
            // margin and each section applies its own padding — same arrangement as
            // the template's `@page { margin:0 }`.
            page.Margin(InvoiceTheme.PageMarginNone);
            page.DefaultTextStyle(InvoiceTheme.BodyStyle);

            // Header / Content / Footer rather than one Column with a spacer:
            // the tail has to sit at the BOTTOM of the page (the template's
            // `margin-top:auto`), and QuestPDF's footer slot does that natively.
            // An .Extend() spacer inside the content column instead forces the
            // column to fill the page and spills onto a second sheet.
            //
            // The header carries no horizontal padding of its own, because the
            // letterhead band bleeds to the paper edge.
            page.Header().Column(header =>
            {
                header.Item().Element(c =>
                    InvoiceComponents.Letterhead(c, invoice.InvoiceNumber, invoice.IssuedAtUtc));
                header.Item().Element(InvoiceComponents.AccentRule);
            });

            page.Content()
                .PaddingHorizontal(InvoiceTheme.BodyPaddingX, Unit.Millimetre)
                .PaddingTop(InvoiceTheme.BodyPaddingTop, Unit.Millimetre)
                .Column(body =>
                {
                    body.Item().Element(Parties);
                    body.Item().PaddingTop(InvoiceTheme.Px(30f)).Element(c =>
                        InvoiceComponents.SectionHeading(c, "SERVICE DETAILS"));
                    body.Item().PaddingTop(InvoiceTheme.Px(9f)).Element(LineItems);
                    body.Item().PaddingTop(InvoiceTheme.Px(34f)).Element(c =>
                        InvoiceComponents.SectionHeading(c, "INVOICE DETAILS"));
                    body.Item().PaddingTop(InvoiceTheme.Px(9f)).Element(Totals);
                });

            page.Footer()
                .PaddingHorizontal(InvoiceTheme.BodyPaddingX, Unit.Millimetre)
                .PaddingBottom(InvoiceTheme.BodyPaddingBottom, Unit.Millimetre)
                .Element(Tail);
        });
    }

    private void Parties(IContainer container)
    {
        var issuedBy = new InvoiceParty(
            provider.BusinessName ?? ProviderPersonName(),
            [
                FriendlyCategory(facts.ServiceCategory),
                $"Provider ID {ShortId.For("PRV", facts.ProviderId)}",
                provider.AddressLine,
                CityLine(provider.Zip, provider.City),
                ContactLine(facts.ProviderEmail, PhoneLine())
            ]);

        var issuedTo = new InvoiceParty(
            ParentName(),
            [
                "Pet Parent",
                $"Customer ID {ShortId.For("PAR", facts.PetParentId)}",
                facts.ParentAddressLine,
                CityLine(facts.ParentZipCode, facts.ParentCity),
                facts.ParentEmail
            ]);

        InvoiceComponents.Parties(container, issuedBy, issuedTo);
    }

    private void LineItems(IContainer container)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn(32);  // Service description
                columns.RelativeColumn(14);  // Booking ref
                columns.RelativeColumn(14);  // Date
                columns.RelativeColumn(12);  // Quantity
                columns.RelativeColumn(13);  // Rate
                columns.RelativeColumn(15);  // Amount
            });

            table.Header(header =>
            {
                InvoiceTable.HeaderCell(header, "SERVICE DESCRIPTION");
                InvoiceTable.HeaderCell(header, "BOOKING REF");
                InvoiceTable.HeaderCell(header, "DATE");
                InvoiceTable.HeaderCell(header, "QUANTITY");
                InvoiceTable.HeaderCell(header, "RATE");
                InvoiceTable.HeaderCell(header, "AMOUNT", right: true);
            });

            // Service + the pet it was for.
            table.Cell().Element(BodyCell).Column(cell =>
            {
                cell.Item().Text(ServiceName())
                    .FontSize(InvoiceTheme.BodySize).SemiBold().FontColor(InvoiceTheme.Ink);

                var pet = PetLine();
                if (!string.IsNullOrWhiteSpace(pet))
                {
                    cell.Item().PaddingTop(InvoiceTheme.Px(3f)).Text(pet).Style(InvoiceTheme.SubLineStyle);
                }
            });

            table.Cell().Element(BodyCell).Text(facts.JobId).Style(InvoiceTheme.BodyStyle);

            // Date, with the time window (or the stay's range) beneath it.
            table.Cell().Element(BodyCell).Column(cell =>
            {
                cell.Item().Text(InvoiceTheme.LongDate(facts.ServiceDate))
                    .FontSize(InvoiceTheme.BodySize).FontColor(InvoiceTheme.Label);
                cell.Item().PaddingTop(InvoiceTheme.Px(3f)).Text(ScheduleLine()).Style(InvoiceTheme.SubLineStyle);
            });

            table.Cell().Element(BodyCell).Text(QuantityText()).Style(InvoiceTheme.BodyStyle);

            table.Cell().Element(BodyCell).Text(
                facts.UnitPrice is null ? "—" : InvoiceTheme.Money(facts.UnitPrice.Value))
                .Style(InvoiceTheme.BodyStyle);

            table.Cell().Element(BodyCell).AlignRight().Text(InvoiceTheme.Money(invoice.Amount))
                .FontSize(InvoiceTheme.BodySize).Bold().FontColor(InvoiceTheme.Ink);
        });
    }

    private void Totals(IContainer container)
    {
        container.Column(column =>
        {
            // Subtotal EQUALS the total: the parent pays the gross, and the
            // Pawfront fee is a commission carved out of it rather than a charge
            // added on top. Showing them apart would imply a surcharge that does
            // not exist.
            InvoiceComponents.TotalsRow(
                column, "Subtotal — service", InvoiceTheme.Money(invoice.Amount),
                topRule: true, bold: true);

            column.Item().PaddingBottom(InvoiceTheme.Px(8f)).Text(
                "MWST, if any, charged by the provider, is included in this amount")
                .FontSize(InvoiceTheme.Px(9.5f)).FontColor(InvoiceTheme.Label).LineHeight(1.5f);

            column.Item().Element(c =>
                InvoiceComponents.GrandTotal(c, "Total Paid", InvoiceTheme.Money(invoice.Amount)));
        });
    }

    private void Tail(IContainer container)
    {
        container.PaddingTop(InvoiceTheme.Px(26f)).Column(column =>
        {
            column.Item().Background(InvoiceTheme.Card).Padding(InvoiceTheme.Px(16f)).PaddingBottom(InvoiceTheme.Px(18f)).Column(card =>
            {
                card.Item().Element(c => InvoiceComponents.SectionHeading(c, "ABOUT THIS INVOICE"));

                var issuer = provider.BusinessName ?? ProviderPersonName();

                card.Item().PaddingTop(InvoiceTheme.Px(7f)).Text(text =>
                {
                    text.DefaultTextStyle(InvoiceTheme.NoteStyle);
                    text.Span("•  This invoice is issued by ");
                    text.Span(issuer).Bold().FontColor(InvoiceTheme.Ink);
                    text.Span(", the service provider.");
                });

                card.Item().PaddingTop(InvoiceTheme.Px(4f)).Text(
                    "•  This invoice is not issued by Pawfront or Littersoft GmbH; Pawfront only " +
                    "delivers the invoice on the provider’s behalf.")
                    .Style(InvoiceTheme.NoteStyle);
            });

            column.Item().PaddingTop(InvoiceTheme.Px(24f)).Element(c => InvoiceComponents.Footer(c, null));
        });
    }

    // ---------------------------------------------------------------- helpers

    private string ServiceName() => facts.IsNightStay
        ? BookingServiceLabel.ResolveNightStay(facts.ServiceDate, facts.EndDate ?? facts.ServiceDate)
        : BookingServiceLabel.Resolve(facts.ServiceType, facts.ServiceItemCode);

    /// <summary>"Milo · Dog (Golden Retriever)", dropping whatever is unknown.</summary>
    private string PetLine()
    {
        var species = string.IsNullOrWhiteSpace(facts.PetBreed)
            ? facts.PetType
            : $"{facts.PetType} ({facts.PetBreed})";

        var parts = new[] { facts.PetName, species }
            .Where(p => !string.IsNullOrWhiteSpace(p));

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// The time window for a single-day booking; the stay's date range for a
    /// night stay, where the clock times are drop-off and pick-up rather than a
    /// window the service occupies.
    /// </summary>
    private string ScheduleLine() => facts.IsNightStay
        ? $"{InvoiceTheme.ShortDate(facts.ServiceDate)} – {InvoiceTheme.ShortDate(facts.EndDate ?? facts.ServiceDate)}"
        : InvoiceTheme.TimeRange(facts.StartTime, facts.EndTime);

    private string QuantityText()
    {
        if (facts.IsNightStay)
        {
            var nights = (int)facts.Quantity;
            return nights == 1 ? "1 night" : $"{nights} nights";
        }

        var hours = facts.Quantity;
        // Trim a trailing ".0" so a whole number reads "4 hours", not "4.0 hours".
        var text = hours == Math.Floor(hours)
            ? ((int)hours).ToString()
            : hours.ToString("0.##");

        return hours == 1m ? "1 hour" : $"{text} hours";
    }

    private string ProviderPersonName()
        => Join(facts.ProviderFirstName, facts.ProviderLastName) ?? "Your provider";

    private string ParentName()
        => Join(facts.ParentFirstName, facts.ParentLastName) ?? "Customer";

    private string? PhoneLine()
        => string.IsNullOrWhiteSpace(facts.ProviderMobileNumber)
            ? null
            : $"{facts.ProviderMobileCountryCode} {facts.ProviderMobileNumber}".Trim();

    private static string? Join(string? first, string? last)
    {
        var name = $"{first} {last}".Trim();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static string? CityLine(string? zip, string? city)
    {
        var line = $"{zip} {city}".Trim();
        return string.IsNullOrWhiteSpace(line) ? null : line;
    }

    private static string? ContactLine(string? email, string? phone)
    {
        var parts = new[] { email, phone }.Where(p => !string.IsNullOrWhiteSpace(p));
        var line = string.Join(" · ", parts);
        return string.IsNullOrWhiteSpace(line) ? null : line;
    }

    private static string FriendlyCategory(string category) => category switch
    {
        "PetSitter" => "Pet Sitter",
        "PetGroomer" => "Pet Groomer",
        "PetTrainer" => "Pet Trainer",
        "PetAdoptionAndSale" => "Pet Adoption & Sale",
        "Vet" => "Vet",
        _ => category
    };

    private static IContainer BodyCell(IContainer container) => InvoiceTable.BodyCell(container);
}
