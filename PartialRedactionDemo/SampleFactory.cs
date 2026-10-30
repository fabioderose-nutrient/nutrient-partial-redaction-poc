using GdPicture14;

namespace PartialRedactionDemo;

public sealed record Sample(string Id, string Title, string Description, bool Scanned);

/// <summary>
/// Fictitious sample documents (test card numbers, example.com addresses, documentation IP ranges).
/// Generated with GdPicture itself: HTML to PDF, and a rasterised copy for the scanned case.
/// </summary>
public static class SampleFactory
{
    public static readonly IReadOnlyList<Sample> All =
    [
        new("dispute-letter", "Card dispute letter", "Customer letter quoting the full card number in the text", false),
        new("case-notes", "Card services case notes", "Internal record with PANs in several formats plus contact data", false),
        new("scanned-form", "Scanned authorization form", "Image-only scan: detection and masking run on OCR", true),
        new("all-types", "All 10 data types", "One line per data type supported by smart redaction", false),
    ];

    public static string PathFor(string directory, string id) => Path.Combine(directory, id + ".pdf");

    public static void EnsureAll(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var (id, html) in new[] { ("dispute-letter", DisputeLetter), ("case-notes", CaseNotes), ("all-types", AllTypes), ("scanned-source", AuthorizationForm) })
        {
            var target = PathFor(directory, id);
            if (!File.Exists(target)) HtmlToPdf(html, target);
        }
        var scanned = PathFor(directory, "scanned-form");
        if (!File.Exists(scanned)) Rasterize(PathFor(directory, "scanned-source"), scanned, 300);
    }

    private static void HtmlToPdf(string html, string target)
    {
        // The HTML engine occasionally reports InvalidParameter on a cold start: retry.
        var status = GdPictureStatus.GenericError;
        for (int attempt = 0; attempt < 4 && status != GdPictureStatus.OK; attempt++)
        {
            using var converter = new GdPictureDocumentConverter
            {
                HtmlPageWidth = 612,
                HtmlPageHeight = 792,
                HtmlPageMarginTop = 54,
                HtmlPageMarginBottom = 54,
                HtmlPageMarginLeft = 60,
                HtmlPageMarginRight = 60,
                HtmlPreferOnePage = false,
            };
            status = converter.LoadFromStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(html)), GdPicture14.DocumentFormat.DocumentFormatHTML);
            if (status == GdPictureStatus.OK) status = converter.SaveAsPDF(target, PdfConformance.PDF1_7);
            if (status != GdPictureStatus.OK) Thread.Sleep(300);
        }
        Check(status, "HTML to PDF");
    }

    /// <summary>Turns a PDF into an image-only PDF (simulates a scanned paper form).</summary>
    private static void Rasterize(string source, string target, float dpi)
    {
        using var input = new GdPicturePDF();
        Check(input.LoadFromFile(source), "load");
        using var output = new GdPicturePDF();
        Check(output.NewPDF(), "new PDF");
        using var imaging = new GdPictureImaging();
        for (int p = 1; p <= input.GetPageCount(); p++)
        {
            input.SelectPage(p);
            int image = input.RenderPageToGdPictureImageEx(dpi, true);
            imaging.ConvertTo8BppGrayScale(image);
            Check(output.AddImageFromGdPictureImage(image, false, true) is { Length: > 0 } ? GdPictureStatus.OK : output.GetStat(), "add page");
            imaging.ReleaseGdPictureImage(image);
        }
        Check(output.SaveToFile(target), "save");
    }

    private static void Check(GdPictureStatus status, string what)
    {
        if (status != GdPictureStatus.OK) throw new InvalidOperationException($"Sample generation, {what}: {status}");
    }

    private const string Css = """
        <style>
          body { font-family: Helvetica, Arial, sans-serif; font-size: 10.5pt; color: #1f2933; line-height: 1.45; }
          .bank { color: #0b3a66; font-size: 17pt; font-weight: bold; margin: 0; }
          .tag { color: #5b6b7b; font-size: 8.5pt; margin: 2px 0 0 0; }
          .rule { border-top: 2px solid #0b3a66; margin: 10px 0 18px 0; }
          h2 { font-size: 12.5pt; color: #0b3a66; margin: 18px 0 8px 0; }
          table { border-collapse: collapse; width: 100%; }
          td, th { border: 1px solid #c9d2db; padding: 6px 8px; text-align: left; vertical-align: top; font-size: 10pt; }
          th { background: #eef2f6; color: #0b3a66; }
          .muted { color: #5b6b7b; font-size: 9pt; }
          .label { width: 34%; color: #3e4c59; }
        </style>
        """;

    private const string Letterhead = """
        <p class="bank">Example Community Bank</p>
        <p class="tag">Card Services &middot; Fictitious sample document</p>
        <div class="rule"></div>
        """;

    private static readonly string DisputeLetter = $$"""
        <html><head>{{Css}}</head><body>
        <p>Jane Doe<br/>1458 Oak Ridge Drive<br/>Columbus, OH 43215</p>
        <p>September 28, 2026</p>
        <p>Example Community Bank<br/>Card Services, Disputes Department<br/>400 Market Street, Denver, CO 80202</p>
        <p><b>Re: Dispute of an unauthorized transaction</b></p>
        <p>Dear Disputes Team,</p>
        <p>I am writing to dispute a charge that I did not authorize. I still have the card in my possession.</p>
        <table>
          <tr><td class="label">Card number</td><td>4111 1111 1111 1111</td></tr>
          <tr><td class="label">Transaction date</td><td>September 21, 2026</td></tr>
          <tr><td class="label">Merchant</td><td>TRVL BOOKINGS ONLINE</td></tr>
          <tr><td class="label">Amount</td><td>$1,284.50</td></tr>
        </table>
        <p>The same merchant also tried to charge my second card, 5555-5555-5555-4444, twice. Both attempts were declined, but I would like this card to be reviewed as well.</p>
        <p>You can reach me at (614) 555-0142 or by email at jane.doe@example.com.</p>
        <p>Please find enclosed a copy of my statement with the disputed transaction highlighted. I would appreciate a provisional credit while your investigation is in progress.</p>
        <p>Sincerely,</p>
        <p>Jane Doe</p>
        <p class="muted">Enclosure: card statement (1 page)</p>
        </body></html>
        """;

    private static readonly string CaseNotes = $$"""
        <html><head>{{Css}}</head><body>
        {{Letterhead}}
        <h2>Case record CS-2026-08817</h2>
        <table>
          <tr><td class="label">Customer</td><td>Robert Miller</td></tr>
          <tr><td class="label">Card number</td><td>4111-1111-1111-1111</td></tr>
          <tr><td class="label">Replacement card number</td><td>6011-1111-1111-1117</td></tr>
          <tr><td class="label">Card number as keyed by agent</td><td>5105105105105100</td></tr>
          <tr><td class="label">Social Security number</td><td>078-05-1120</td></tr>
          <tr><td class="label">Phone</td><td>+1 303 555 0187</td></tr>
          <tr><td class="label">Email</td><td>r.miller@example.org</td></tr>
          <tr><td class="label">Mailing address</td><td>2210 Walnut Street, Portland, OR 97205</td></tr>
          <tr><td class="label">Online banking session IP</td><td>203.0.113.42</td></tr>
        </table>
        <h2>Agent notes</h2>
        <p>10/02 09:14 &ndash; Customer called from +1 303 555 0187 reporting card 5555-5555-5555-4444 lost at the airport. Card blocked, replacement 6011-1111-1111-1117 ordered with express delivery.</p>
        <p>10/02 09:21 &ndash; Customer confirmed the last online banking login came from 203.0.113.42. No suspicious activity found on the account.</p>
        <p>10/03 15:40 &ndash; Follow-up email sent to r.miller@example.org with the replacement tracking number.</p>
        </body></html>
        """;

    private static readonly string AuthorizationForm = $$"""
        <html><head>{{Css}}</head><body>
        {{Letterhead}}
        <h2>Recurring payment authorization form</h2>
        <p>I authorize Example Community Bank to charge the card below for the monthly premium of my account protection plan.</p>
        <table>
          <tr><td class="label">Cardholder name</td><td>Maria Gonzalez</td></tr>
          <tr><td class="label">Card number</td><td>5555 5555 5555 4444</td></tr>
          <tr><td class="label">Expiration date</td><td>08/29</td></tr>
          <tr><td class="label">Billing address</td><td>77 Elm Avenue, Austin, TX 78701</td></tr>
          <tr><td class="label">Phone</td><td>(512) 555-0163</td></tr>
          <tr><td class="label">Email</td><td>maria.gonzalez@example.net</td></tr>
          <tr><td class="label">Amount</td><td>$14.95 per month</td></tr>
        </table>
        <p style="margin-top:28px">Signature: ______________________________ &nbsp;&nbsp; Date: 10/01/2026</p>
        <p class="muted">Scanned at branch 012 &ndash; Downtown</p>
        </body></html>
        """;

    private static readonly string AllTypes = $$"""
        <html><head>{{Css}}</head><body>
        {{Letterhead}}
        <h2>Smart redaction data types</h2>
        <table>
          <tr><th class="label">Data type</th><th>Sample value</th></tr>
          <tr><td class="label">Credit card number</td><td>4111 1111 1111 1111</td></tr>
          <tr><td class="label">IBAN</td><td>GB82 WEST 1234 5698 7654 32</td></tr>
          <tr><td class="label">Social Security number</td><td>219-09-9999</td></tr>
          <tr><td class="label">Vehicle Identification Number</td><td>1HGBH41JXMN109186</td></tr>
          <tr><td class="label">IP address</td><td>192.168.1.12</td></tr>
          <tr><td class="label">Email address</td><td>alex@example.com</td></tr>
          <tr><td class="label">URI</td><td>https://example.com/accounts/1234</td></tr>
          <tr><td class="label">VAT ID</td><td>DE 136 695 976</td></tr>
          <tr><td class="label">Phone number</td><td>+1 212 555 0198</td></tr>
          <tr><td class="label">Postal address</td><td>123 Main Street, Boston, MA 02110</td></tr>
        </table>
        </body></html>
        """;
}
