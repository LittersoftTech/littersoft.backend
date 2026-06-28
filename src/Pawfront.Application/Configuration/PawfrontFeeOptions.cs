namespace Pawfront.Application.Configuration;

/// <summary>
/// Platform fee configuration, bound from the <c>Payments</c> config section.
/// The Pawfront fee shown on a booking's payment details is
/// <see cref="PawfrontFeePercentage"/> percent of the booking's total amount.
/// </summary>
public sealed class PawfrontFeeOptions
{
    /// <summary>Config section name to bind from.</summary>
    public const string SectionName = "Payments";

    /// <summary>
    /// Pawfront's commission as a percentage of the booking total (e.g. <c>10</c>
    /// means 10%). Defaults to 0 when the section is absent, so a missing config
    /// yields a zero fee rather than throwing.
    /// </summary>
    public decimal PawfrontFeePercentage { get; set; }
}
