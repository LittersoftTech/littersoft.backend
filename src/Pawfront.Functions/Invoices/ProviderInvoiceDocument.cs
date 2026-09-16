using Pawfront.Application.Notifications;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Pawfront.Functions.Invoices;

/// <summary>
/// The PROVIDER's invoice: issued by LITTERSOFT GmbH to the service provider for
/// the Pawfront fee earned on ONE completed job. One invoice = one service,
/// matching the pet-parent invoice — not a billing-period roll-up.
///
/// Why the provider is billed at all: on a CASH job the customer hands the
/// provider the full amount, fee included, so the fee has to be invoiced back.
/// Once digital payouts go live the same fee is netted off the payout instead and
/// this document stops being raised.
///
/// Nothing is payable while the 100% launch offer runs, so it carries no due date,
/// no payment terms and no how-to-pay block.
///
/// The fee is <c>Payments:PawfrontFeePercentage</c> of the gross — the figure
/// frozen on the invoice row at payment time, not re-derived — and the MWST line
/// is computed on the fee AFTER the discount, so it reads CHF 0.00 while the offer
/// runs. The supplied template hardcoded 5% + 8.1%; the 5% is superseded by the
/// configured rate, and the 8.1% MWST rate is kept.
///
/// Deliberately absent: the MWST / UID identifiers for Littersoft and for the
/// provider. Neither exists anywhere in the schema, and printing an invented tax
/// identifier would be worse than omitting the line.
/// </summary>
internal sealed class ProviderInvoiceDocument(
    InvoiceBookingFacts facts,
    ClaimedInvoice invoice,
    InvoiceProviderIdentity provider,
    LittersoftIdentity littersoft,
    decimal feePercentage,
    decimal discountPercentage,
    decimal mwstPercentage) : IDocument
{
    public DocumentMetadata GetMetadata() => new()
    {
        Title = $"Littersoft_Invoice_{invoice.InvoiceNumber}",
        Author = littersoft.Name,
        Subject = "Pawfront service fee"
    };

    /// <summary>What the fee would be before the launch offer.</summary>
    private decimal Fee => invoice.PawfrontFee;

    private decimal Discount => Math.Round(Fee * discountPercentage / 100m, 2, MidpointRounding.AwayFromZero);

    /// <summary>The fee actually charged — zero while the 100% offer runs.</summary>
    private decimal NetFee => Fee - Discount;

    private decimal Mwst => Math.Round(NetFee * mwstPercentage / 100m, 2, MidpointRounding.AwayFromZero);

    private decimal TotalDue => NetFee + Mwst;

    public void Compose(IDocumentContainer document)
    {
        document.Page(page =>
        {
            page.Size(PageSizes.A4);
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
                        InvoiceComponents.SectionHeading(c, "SERVICE YOU PROVIDED"));
                    body.Item().PaddingTop(InvoiceTheme.Px(9f)).Element(JobRow);
                    body.Item().PaddingTop(InvoiceTheme.Px(34f)).Element(c =>
                        InvoiceComponents.SectionHeading(c, "INVOICE DETAILS"));
                    body.Item().PaddingTop(InvoiceTheme.Px(9f)).Element(FeeSummary);
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
            littersoft.Name,
            [
                littersoft.AddressLine,
                CityLine(littersoft.Zip, littersoft.City),
                ContactLine(littersoft.Email, littersoft.Website)
            ]);

        // The provider's business name heads the block when they have one, with
        // their own name on the line beneath — a freelancer has no business name,
        // so theirs heads it instead and the sub-line drops the duplicate.
        var businessName = provider.BusinessName;
        var personName = ProviderPersonName();

        var subLine = businessName is null
            ? FriendlyCategory(facts.ServiceCategory)
            : $"{personName} · {FriendlyCategory(facts.ServiceCategory)}";

        var issuedTo = new InvoiceParty(
            businessName ?? personName,
            [
                subLine,
                $"Provider ID {ShortId.For("PRV", facts.ProviderId)}",
                provider.AddressLine,
                CityLine(provider.Zip, provider.City),
                facts.ProviderEmail
            ]);

        InvoiceComponents.Parties(container, issuedBy, issuedTo);
    }

    private void JobRow(IContainer container)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                // Widened from the template's 10/11% for the two fee columns:
                // "PAWFRONT" breaks mid-word at that width once letter-spacing is
                // applied, and a hyphenated column heading looks like a defect.
                // The width comes back off "Your customer", which has room.
                columns.RelativeColumn(10);  // Date
                columns.RelativeColumn(14);  // Job ID
                columns.RelativeColumn(18);  // Service
                columns.RelativeColumn(19);  // Your customer
                columns.RelativeColumn(15);  // Gross collected
                columns.RelativeColumn(11);  // Fee %
                columns.RelativeColumn(13);  // Fee amount
            });

            table.Header(header =>
            {
                InvoiceTable.HeaderCell(header, "DATE");
                InvoiceTable.HeaderCell(header, "JOB ID");
                InvoiceTable.HeaderCell(header, "SERVICE");
                InvoiceTable.HeaderCell(header, "YOUR CUSTOMER");
                InvoiceTable.HeaderCell(header, "GROSS AMOUNT YOU COLLECTED");
                InvoiceTable.HeaderCell(header, "PAWFRONT FEE");
                InvoiceTable.HeaderCell(header, "PAWFRONT FEE", right: true);
            });

            table.Cell().Element(InvoiceTable.CompactCell)
                .Text(InvoiceTheme.ShortDate(facts.ServiceDate))
                .FontSize(InvoiceTheme.BodySize).FontColor(InvoiceTheme.Label);

            table.Cell().Element(InvoiceTable.CompactCell)
                .Text(facts.JobId)
                .FontSize(InvoiceTheme.Px(9f)).FontColor(InvoiceTheme.Faint);

            table.Cell().Element(InvoiceTable.CompactCell)
                .Text(ServiceName())
                .FontSize(InvoiceTheme.BodySize).SemiBold().FontColor(InvoiceTheme.Ink);

            table.Cell().Element(InvoiceTable.CompactCell)
                .Text(ParentName()).Style(InvoiceTheme.BodyStyle);

            table.Cell().Element(InvoiceTable.CompactCell)
                .Text(InvoiceTheme.Money(invoice.Amount)).Style(InvoiceTheme.BodyStyle);

            table.Cell().Element(InvoiceTable.CompactCell)
                .Text($"{Trim(feePercentage)}%")
                .FontSize(InvoiceTheme.BodySize).FontColor(InvoiceTheme.Label);

            table.Cell().Element(InvoiceTable.CompactCell).AlignRight()
                .Text(InvoiceTheme.Money(Fee))
                .FontSize(InvoiceTheme.BodySize).Bold().FontColor(InvoiceTheme.Ink);
        });
    }

    private void FeeSummary(IContainer container)
    {
        container.Column(column =>
        {
            InvoiceComponents.TotalsRow(
                column, "Service fee", InvoiceTheme.Money(Fee), topRule: true, bold: true);

            if (discountPercentage > 0)
            {
                InvoiceComponents.TotalsRow(
                    column,
                    $"Discount — launch offer ({Trim(discountPercentage)}%)",
                    "−" + InvoiceTheme.Money(Discount));
            }

            InvoiceComponents.TotalsRow(
                column,
                $"MWST {Trim(mwstPercentage)}%",
                InvoiceTheme.Money(Mwst),
                labelSuffix: $"on {InvoiceTheme.Money(NetFee)}");

            column.Item().Element(c =>
                InvoiceComponents.GrandTotal(c, "Total due", InvoiceTheme.Money(TotalDue)));
        });
    }

    private void Tail(IContainer container)
    {
        container.PaddingTop(InvoiceTheme.Px(26f)).Column(column =>
        {
            column.Item().Background(InvoiceTheme.Card).Padding(InvoiceTheme.Px(16f)).PaddingBottom(InvoiceTheme.Px(18f)).Row(row =>
            {
                // Two columns: the plain-language answer to the question a cash-job
                // fee invoice always raises, and the formal terms beside it. One
                // full-width column across 182mm of A4 runs far too long to scan.
                row.RelativeItem(125).Column(why =>
                {
                    why.Item().Element(c =>
                        InvoiceComponents.SectionHeading(c, "WHY AM I GETTING THIS INVOICE?"));

                    why.Item().PaddingTop(InvoiceTheme.Px(7f)).Text(
                        "Pawfront charges a small fee on every booking made through our platform.")
                        .Style(InvoiceTheme.NoteStyle);

                    why.Item().PaddingTop(InvoiceTheme.Px(7f)).Text(
                        "When a customer pays by card, we simply keep our fee and send you the rest. " +
                        "But this booking was paid in cash, so you received the full amount directly " +
                        "from your customer — we had no chance to take our fee.")
                        .Style(InvoiceTheme.NoteStyle);

                    why.Item().PaddingTop(InvoiceTheme.Px(7f)).Text(
                        "So we’re billing you for it instead. This invoice is our fee on that cash " +
                        "booking. Nothing extra has been added.")
                        .Style(InvoiceTheme.NoteStyle);
                });

                row.ConstantItem(9, Unit.Millimetre);

                row.RelativeItem(100)
                    .BorderLeft(1).BorderColor(InvoiceTheme.Rule)
                    .PaddingLeft(9, Unit.Millimetre)
                    .Column(about =>
                    {
                        about.Item().Element(c =>
                            InvoiceComponents.SectionHeading(c, "ABOUT THIS INVOICE"));

                        if (discountPercentage > 0)
                        {
                            about.Item().PaddingTop(InvoiceTheme.Px(7f)).Text(text =>
                            {
                                text.DefaultTextStyle(InvoiceTheme.NoteStyle);
                                text.Span("•  A ");
                                text.Span($"{Trim(discountPercentage)}% launch-offer discount")
                                    .Bold().FontColor(InvoiceTheme.Ink);
                                text.Span(" is applied to the Pawfront fee, so ");
                                text.Span("nothing is payable").Bold().FontColor(InvoiceTheme.Ink);
                                text.Span(". The fee above is shown for your records.");
                            });
                        }

                        about.Item().PaddingTop(InvoiceTheme.Px(4f)).Text(text =>
                        {
                            text.DefaultTextStyle(InvoiceTheme.NoteStyle);
                            text.Span("•  Only ");
                            text.Span("completed and paid").Bold().FontColor(InvoiceTheme.Ink);
                            text.Span(" jobs are billed. Pending, unpaid, expired and declined jobs " +
                                      "carry no fee and raise no invoice.");
                        });
                    });
            });

            column.Item().PaddingTop(InvoiceTheme.Px(24f)).Element(c => InvoiceComponents.Footer(c, littersoft.FooterText));
        });
    }

    // ---------------------------------------------------------------- helpers

    private string ServiceName() => facts.IsNightStay
        ? BookingServiceLabel.ResolveNightStay(facts.ServiceDate, facts.EndDate ?? facts.ServiceDate)
        : BookingServiceLabel.Resolve(facts.ServiceType, facts.ServiceItemCode);

    private string ProviderPersonName()
        => Join(facts.ProviderFirstName, facts.ProviderLastName) ?? "Provider";

    private string ParentName()
        => Join(facts.ParentFirstName, facts.ParentLastName) ?? "Customer";

    /// <summary>"10" rather than "10.00"; "8.1" stays "8.1".</summary>
    private static string Trim(decimal value)
        => value == Math.Floor(value) ? ((int)value).ToString() : value.ToString("0.##");

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

    private static string? ContactLine(string? a, string? b)
    {
        var parts = new[] { a, b }.Where(p => !string.IsNullOrWhiteSpace(p));
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
}
