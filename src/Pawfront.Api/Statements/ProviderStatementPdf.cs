using System.Globalization;
using Pawfront.Application.Earnings;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Pawfront.Api.Statements;

internal static class ProviderStatementPdf
{
    public static byte[] Render(Guid providerId, DateOnly? from, DateOnly? to,
        IReadOnlyList<ProviderEarningsBookingRow> rows)
    {
        // Same licence configuration as the booking invoice host.
        QuestPDF.Settings.License = LicenseType.Community;
        return Document.Create(document => document.Page(page =>
        {
            page.Size(PageSizes.A4.Landscape());
            page.Margin(30);
            page.DefaultTextStyle(s => s.FontSize(9));
            page.Header().PaddingBottom(15).Column(header =>
            {
                header.Item().Text("PAWFRONT | Earnings statement").FontSize(21).Bold().FontColor("#176B58");
                header.Item().Text($"Provider: {providerId}");
                header.Item().Text($"Service dates: {Date(from, "Beginning")} to {Date(to, "Present")} | Currency: CHF");
            });
            page.Content().Column(body =>
            {
                body.Spacing(12);
                body.Item().Text("Cash and private jobs: no Pawfront fee deducted. Unpaid and ignored amounts are potential values, not received earnings.");
                foreach (var group in new[] { "Received", "Pending", "Unpaid", "Ignored" })
                {
                    var items = rows.Where(r => ProviderStatement.Group(r.Status) == group).ToArray();
                    body.Item().Text($"{group} | {items.Length} jobs | Gross {Money(items.Sum(r => r.GrossAmount))} | Fee {Money(items.Sum(ProviderStatement.Fee))} | Net {Money(items.Sum(ProviderStatement.Net))}")
                        .Bold().FontSize(12).FontColor("#176B58");
                    if (items.Length == 0)
                    {
                        body.Item().Text("No jobs in this group.");
                        continue;
                    }
                    body.Item().Table(table =>
                    {
                        table.ColumnsDefinition(c =>
                        {
                            c.ConstantColumn(66); c.ConstantColumn(70); c.RelativeColumn(2);
                            c.RelativeColumn(2); c.RelativeColumn(2); c.RelativeColumn();
                            c.ConstantColumn(58); c.ConstantColumn(50); c.ConstantColumn(58);
                        });
                        table.Header(h =>
                        {
                            foreach (var label in new[] { "Date", "Job", "Customer / Pet", "Service", "Status", "Payment", "Gross", "Fee", "Net" })
                                h.Cell().Background("#E9F2EF").Padding(5).Text(label).Bold();
                        });
                        foreach (var row in items)
                        {
                            foreach (var value in new[] {
                                Date(row.ServiceDate, ""), row.JobId,
                                $"{row.CustomerName ?? "-"} / {row.PetName ?? "-"}",
                                $"{row.ServiceCategory} / {row.SubCategory}" + (row.IsPrivate ? " (Private)" : ""),
                                row.Status, row.PaymentMethod ?? "-", Money(row.GrossAmount),
                                Money(ProviderStatement.Fee(row)), Money(ProviderStatement.Net(row)) })
                                table.Cell().BorderBottom(0.5f).BorderColor("#DDDDDD").Padding(5).Text(value);
                        }
                    });
                    var unpriced = items.Count(r => r.GrossAmount is null);
                    if (unpriced > 0) body.Item().Text($"{unpriced} unpriced job(s), shown as '-' and excluded from totals.");
                }
            });
            page.Footer().PaddingTop(10).AlignRight().Text(t =>
            {
                t.Span("Pawfront statement | Page "); t.CurrentPageNumber(); t.Span(" / "); t.TotalPages();
            });
        })).GeneratePdf();
    }

    private static string Date(DateOnly? date, string fallback) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? fallback;
    private static string Money(decimal? amount) => amount?.ToString("N2", CultureInfo.InvariantCulture) ?? "-";
}


