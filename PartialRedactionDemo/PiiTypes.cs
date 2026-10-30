using GdPicture14;

namespace PartialRedactionDemo;

// Declaration order = priority when two detectors flag the same area
// (e.g. a card number also matching the phone pattern stays a card number).
public enum PiiType { CreditCard, IBAN, SSN, VIN, IPAddress, Email, URI, VatId, Phone, PostalAddress }

public sealed record PiiTypeInfo(PiiType Type, string Id, string Label, string PartialRule, Action<GdPicturePDF.SmartRedactionOptions> Enable);

public static class PiiTypes
{
    public static readonly IReadOnlyList<PiiTypeInfo> All =
    [
        new(PiiType.CreditCard, "creditCard", "Credit card number (PAN)", "Keeps the last 4 digits", o => o.RedactCreditCardNumbers = true),
        new(PiiType.IBAN, "iban", "IBAN", "Keeps the last 4 characters", o => o.RedactIBANs = true),
        new(PiiType.SSN, "ssn", "Social Security number", "Keeps the last 4 digits", o => o.RedactSocialSecurityNumbers = true),
        new(PiiType.VIN, "vin", "Vehicle Identification Number", "Keeps the last 4 characters", o => o.RedactVehicleIdentificationNumbers = true),
        new(PiiType.IPAddress, "ip", "IP address", "Keeps the last group", o => o.RedactIPAddresses = true),
        new(PiiType.Email, "email", "Email address", "Keeps the domain", o => o.RedactEmailAddresses = true),
        new(PiiType.URI, "uri", "URI", "Keeps the last path segment", o => o.RedactURIs = true),
        new(PiiType.VatId, "vat", "VAT ID", "Keeps the last group", o => o.RedactVatIDs = true),
        new(PiiType.Phone, "phone", "Phone number", "Keeps the last 4 digits", o => o.RedactPhoneNumbers = true),
        new(PiiType.PostalAddress, "postal", "Postal address", "Keeps state and ZIP code", o => o.RedactPostalAddresses = true),
    ];

    public static PiiTypeInfo Get(PiiType type) => All.First(t => t.Type == type);

    public static PiiTypeInfo? FromId(string id) => All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
}
