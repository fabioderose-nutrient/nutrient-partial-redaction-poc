using System.Diagnostics;
using System.Reflection;
using GdPicture14;

namespace PartialRedactionDemo;

public sealed record Box(float Left, float Top, float Width, float Height)
{
    public float Right => Left + Width;
    public float Bottom => Top + Height;
    public static Box FromEdges(float l, float t, float r, float b) => new(l, t, r - l, b - t);
}

public sealed record PageInfo(int Number, float Width, float Height);

public sealed record Finding(
    int Page, string Type, string Label, Box Area,
    string Original, string PartialDisplay, string Kept,
    IReadOnlyList<Box> MaskBoxes, string Mode,
    string FullTextLeft, string PartialTextLeft);

public sealed record RedactionResult(
    byte[] Original, byte[] Full, byte[] Partial,
    IReadOnlyList<PageInfo> Pages, IReadOnlyList<Finding> Findings,
    string Engine, long DetectMs, long FullMs, long PartialMs, bool OcrUsed);

/// <summary>
/// Full vs partial smart redaction with GdPicture.NET.
/// Full = SmartRedaction as shipped today. Partial = the PartialRedaction option when the
/// installed GdPicture exposes it (next release), otherwise the same behaviour built on the
/// public API of 14.4.9: SmartRedaction detects (marks only), glyph geometry shrinks each mark
/// to the characters to hide, ApplyRedaction burns the result.
/// </summary>
public sealed class RedactionEngine(string ocrResourcePath)
{
    private const string RedactionSubtype = "GdPicture-AnnotationTypeRedaction";
    private const int OcrDpi = 300;

    // Present from the GdPicture release that ships the PartialRedaction option; null on 14.4.9.
    public static readonly PropertyInfo? NativePartialOption =
        typeof(GdPicturePDF.SmartRedactionOptions).GetProperty("PartialRedaction");

    public static string EngineLabel => NativePartialOption is null
        ? "GdPicture.NET " + GdPictureVersion + " + partial layer (public API)"
        : "GdPicture.NET " + GdPictureVersion + " native PartialRedaction";

    public static string GdPictureVersion =>
        typeof(GdPicturePDF).Assembly.GetName().Version?.ToString(3) ?? "14";

    private sealed record RawDetection(int Page, PiiType Type, Box Rect);

    public RedactionResult Process(byte[] source, IReadOnlyCollection<PiiType> types)
    {
        var sw = Stopwatch.StartNew();
        var detections = Detect(source, types);
        long detectMs = sw.ElapsedMilliseconds;

        sw.Restart();
        byte[] full = SmartRedact(source, types, partial: false);
        long fullMs = sw.ElapsedMilliseconds;

        sw.Restart();
        var (partial, plans, ocrUsed) = PartialRedact(source, types, detections);
        long partialMs = sw.ElapsedMilliseconds;

        var pages = ReadPages(source);
        var fullLeft = TextLeft(full, detections.Select(d => (d.Page, d.Rect)).ToList());
        var partialLeft = TextLeft(partial, detections.Select(d => (d.Page, d.Rect)).ToList());

        var findings = detections.Select((d, i) =>
        {
            var plan = plans[i];
            var info = PiiTypes.Get(d.Type);
            return new Finding(d.Page, info.Id, info.Label, d.Rect,
                plan.Original, plan.Display, plan.Kept, plan.MaskBoxes, plan.Mode,
                fullLeft[i], partialLeft[i]);
        }).ToList();

        return new RedactionResult(source, full, partial, pages, findings,
            EngineLabel, detectMs, fullMs, partialMs, ocrUsed);
    }

    // ---------------------------------------------------------------- detection

    /// <summary>One SmartRedaction pass per data type (marks only, run in parallel) so every area carries its type.</summary>
    private List<RawDetection> Detect(byte[] source, IReadOnlyCollection<PiiType> types)
    {
        var bag = new System.Collections.Concurrent.ConcurrentBag<RawDetection>();
        Parallel.ForEach(types, type =>
        {
            using var pdf = Load(source);
            var options = Options(new[] { type }, immediate: false);
            Check(pdf.SmartRedaction(options), "SmartRedaction");
            Prepare(pdf);
            for (int p = 1; p <= pdf.GetPageCount(); p++)
            {
                pdf.SelectPage(p);
                for (int a = 0; a < pdf.GetAnnotationCount(); a++)
                {
                    if (pdf.GetAnnotationSubType(a) != RedactionSubtype) continue;
                    float l = 0, t = 0, w = 0, h = 0;
                    pdf.GetAnnotationRect(a, ref l, ref t, ref w, ref h);
                    bag.Add(new RawDetection(p, type, new Box(l, t, w, h)));
                }
            }
        });

        // Same area flagged by two detectors: keep the higher-priority type.
        var result = new List<RawDetection>();
        foreach (var d in bag.OrderBy(d => d.Type))
            if (!result.Any(r => r.Page == d.Page && Overlap(r.Rect, d.Rect) > 0.6f))
                result.Add(d);

        return result.OrderBy(d => d.Page).ThenBy(d => Math.Round(d.Rect.Top / 4)).ThenBy(d => d.Rect.Left).ToList();
    }

    // ---------------------------------------------------------------- redaction

    private byte[] SmartRedact(byte[] source, IReadOnlyCollection<PiiType> types, bool partial)
    {
        using var pdf = Load(source);
        var options = Options(types, immediate: true);
        if (partial) NativePartialOption!.SetValue(options, true);
        Check(pdf.SmartRedaction(options), "SmartRedaction");
        return Save(pdf);
    }

    private sealed record Plan(string Original, string Display, string Kept, IReadOnlyList<Box> MaskBoxes, string Mode);

    private (byte[] Pdf, List<Plan> Plans, bool OcrUsed) PartialRedact(byte[] source, IReadOnlyCollection<PiiType> types, List<RawDetection> detections)
    {
        using var pdf = Load(source);
        Prepare(pdf);
        var plans = new Plan[detections.Count];
        bool ocrUsed = false;

        foreach (var pageGroup in detections.Select((d, i) => (d, i)).GroupBy(x => x.d.Page))
        {
            pdf.SelectPage(pageGroup.Key);
            var words = ReadWords(pdf);
            if (words.Count == 0)
            {
                // Image-only page: add an OCR text layer to get character geometry.
                Check(pdf.OcrPage("eng", ocrResourcePath, "", OcrDpi), "OcrPage");
                ocrUsed = true;
                words = ReadWords(pdf);
            }

            foreach (var (d, i) in pageGroup)
            {
                plans[i] = PlanFor(d, words);
                foreach (var b in plans[i].MaskBoxes)
                    Check(pdf.AddRedactionRegion(b.Left, b.Top, b.Width, b.Height), "AddRedactionRegion");
            }
        }

        if (NativePartialOption is not null)
            return (SmartRedact(source, types, partial: true), plans.ToList(), ocrUsed);

        if (detections.Count > 0) Check(pdf.ApplyRedaction(), "ApplyRedaction");
        return (Save(pdf), plans.ToList(), ocrUsed);
    }

    private static Plan PlanFor(RawDetection d, List<(Box Box, List<Glyph> Glyphs)> words)
    {
        var r = d.Rect;
        var glyphs = new List<Glyph>();
        foreach (var (box, wordGlyphs) in words)
        {
            float cy = (box.Top + box.Bottom) / 2;
            if (cy < r.Top || cy > r.Bottom) continue;
            glyphs.AddRange(wordGlyphs.Where(g => (g.X0 + g.X1) / 2 >= r.Left && (g.X0 + g.X1) / 2 <= r.Right));
        }

        if (glyphs.Count == 0)
            // No character geometry: never leak, redact the whole area.
            return new Plan("", "(fully redacted)", "", new[] { r }, "full-fallback");

        glyphs = glyphs.OrderBy(g => Math.Round(g.Top / 4)).ThenBy(g => g.X0).ToList();
        // Sentence punctuation glued to the value ("1881." / "4444,") is not part of it.
        while (glyphs.Count > 1 && ".,;:!?)".Contains(glyphs[^1].C) && glyphs[^1].Word == glyphs[^2].Word) glyphs.RemoveAt(glyphs.Count - 1);
        var keep = PartialRules.KeepMask(d.Type, glyphs);

        var boxes = new List<Box>();
        int i = 0;
        while (i < glyphs.Count)
        {
            if (keep[i]) { i++; continue; }
            int j = i;
            while (j + 1 < glyphs.Count && !keep[j + 1] && glyphs[j + 1].Word == glyphs[i].Word) j++;

            bool keptBefore = i > 0 && keep[i - 1] && glyphs[i - 1].Word == glyphs[i].Word;
            bool keptAfter = j + 1 < glyphs.Count && keep[j + 1] && glyphs[j + 1].Word == glyphs[i].Word;
            // 1 pt margin like SmartRedaction, but never bleed onto a character that stays visible.
            float left = glyphs[i].X0 + (keptBefore ? 0.2f : -1f);
            float right = glyphs[j].X1 + (keptAfter ? -0.2f : 1f);
            float top = glyphs.Skip(i).Take(j - i + 1).Min(g => g.Top) - 1f;
            float bottom = glyphs.Skip(i).Take(j - i + 1).Max(g => g.Bottom) + 1f;
            boxes.Add(Box.FromEdges(left, top, right, bottom));
            i = j + 1;
        }

        var kept = new string(glyphs.Where((g, k) => keep[k]).Select(g => g.C).ToArray());
        return new Plan(
            PartialRules.Display(glyphs, keep, masked: false),
            PartialRules.Display(glyphs, keep, masked: true),
            kept, boxes, "partial");
    }

    // ---------------------------------------------------------------- text geometry

    private const TextExtractionOutputInfo WordInfo =
        TextExtractionOutputInfo.IncludeWordBounds | TextExtractionOutputInfo.IncludeWordString |
        TextExtractionOutputInfo.IncludeGlyphWidths | TextExtractionOutputInfo.IncludeGlyphCharacters;

    /// <summary>Words of the selected page with per-character x extents.</summary>
    private static List<(Box Box, List<Glyph> Glyphs)> ReadWords(GdPicturePDF pdf)
    {
        const char sep = '\u001F';
        var raw = pdf.GetPageTextWithCoords(sep.ToString(), WordInfo) ?? "";
        var words = new List<(Box, List<Glyph>)>();
        int index = 0;
        foreach (var line in raw.Split('\n'))
        {
            var f = line.TrimEnd('\r').Split(sep);
            if (f.Length < 10 || string.IsNullOrWhiteSpace(f[8])) continue;
            var xs = new[] { P(f[0]), P(f[2]), P(f[4]), P(f[6]) };
            var ys = new[] { P(f[1]), P(f[3]), P(f[5]), P(f[7]) };
            var box = Box.FromEdges(xs.Min(), ys.Min(), xs.Max(), ys.Max());

            var rest = f.Skip(9).ToArray();
            int n = rest.Length / 2;
            var widths = rest.Take(n).Select(P).ToArray();
            var chars = rest.Skip(n).Take(n).Select(s => s.Length > 0 ? s[0] : ' ').ToArray();
            if (n == 0) { chars = f[8].ToCharArray(); widths = chars.Select(_ => box.Width / chars.Length).ToArray(); n = chars.Length; }

            float total = widths.Sum();
            float scale = total > 0 ? box.Width / total : 1f;
            float x = box.Left;
            var glyphs = new List<Glyph>(n);
            for (int k = 0; k < n; k++)
            {
                float w = widths[k] * scale;
                if (!char.IsWhiteSpace(chars[k]))
                    glyphs.Add(new Glyph(chars[k], x, x + w, box.Top, box.Bottom, index));
                x += w;
            }
            if (glyphs.Count > 0) words.Add((box, glyphs));
            index++;
        }
        return words;
    }

    private static float P(string s) =>
        float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0f;

    /// <summary>Text still extractable inside each detected area (proves the hidden characters are gone, not just covered).</summary>
    private static List<string> TextLeft(byte[] pdfBytes, List<(int Page, Box Rect)> areas)
    {
        using var pdf = Load(pdfBytes);
        Prepare(pdf);
        var list = new List<string>();
        foreach (var (page, r) in areas)
        {
            pdf.SelectPage(page);
            var words = ReadWords(pdf);
            var chars = words
                .Where(w => (w.Box.Top + w.Box.Bottom) / 2 >= r.Top && (w.Box.Top + w.Box.Bottom) / 2 <= r.Bottom)
                .SelectMany(w => w.Glyphs)
                .Where(g => (g.X0 + g.X1) / 2 >= r.Left && (g.X0 + g.X1) / 2 <= r.Right)
                .OrderBy(g => Math.Round(g.Top / 4)).ThenBy(g => g.X0)
                .ToList();
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < chars.Count; i++)
            {
                if (i > 0 && chars[i].Word != chars[i - 1].Word) sb.Append(' ');
                sb.Append(chars[i].C);
            }
            list.Add(sb.ToString());
        }
        return list;
    }

    // ---------------------------------------------------------------- rendering

    public static List<PageInfo> ReadPages(byte[] pdfBytes)
    {
        using var pdf = Load(pdfBytes);
        Prepare(pdf);
        var pages = new List<PageInfo>();
        for (int p = 1; p <= pdf.GetPageCount(); p++)
        {
            pdf.SelectPage(p);
            pages.Add(new PageInfo(p, pdf.GetPageWidth(), pdf.GetPageHeight()));
        }
        return pages;
    }

    public static void RenderPages(byte[] pdfBytes, string directory, string prefix, float dpi)
    {
        Directory.CreateDirectory(directory);
        using var pdf = Load(pdfBytes);
        using var imaging = new GdPictureImaging();
        for (int p = 1; p <= pdf.GetPageCount(); p++)
        {
            pdf.SelectPage(p);
            int image = pdf.RenderPageToGdPictureImageEx(dpi, true);
            if (image == 0) throw new InvalidOperationException("Page rendering failed: " + pdf.GetStat());
            try { Check(imaging.SaveAsPNG(image, Path.Combine(directory, $"{prefix}-{p}.png")), "SaveAsPNG"); }
            finally { imaging.ReleaseGdPictureImage(image); }
        }
    }

    // ---------------------------------------------------------------- helpers

    private GdPicturePDF.SmartRedactionOptions Options(IEnumerable<PiiType> types, bool immediate)
    {
        var options = new GdPicturePDF.SmartRedactionOptions
        {
            ResourcePath = ocrResourcePath,
            Immediate = immediate,
        };
        foreach (var t in types) PiiTypes.Get(t).Enable(options);
        return options;
    }

    private static GdPicturePDF Load(byte[] bytes)
    {
        var pdf = new GdPicturePDF();
        var status = pdf.LoadFromStream(new MemoryStream(bytes), true);
        if (status != GdPictureStatus.OK) { pdf.Dispose(); throw new InvalidOperationException("Could not open the PDF: " + status); }
        if (pdf.IsEncrypted()) { pdf.Dispose(); throw new InvalidOperationException("Encrypted PDFs are not supported in this demo."); }
        return pdf;
    }

    private static void Prepare(GdPicturePDF pdf)
    {
        pdf.SetMeasurementUnit(PdfMeasurementUnit.PdfMeasurementUnitPoint);
        pdf.SetOrigin(PdfOrigin.PdfOriginTopLeft);
    }

    private static byte[] Save(GdPicturePDF pdf)
    {
        using var ms = new MemoryStream();
        Check(pdf.SaveToStream(ms), "SaveToStream");
        return ms.ToArray();
    }

    private static void Check(GdPictureStatus status, string what)
    {
        if (status != GdPictureStatus.OK) throw new InvalidOperationException($"{what} failed: {status}");
    }

    private static float Overlap(Box a, Box b)
    {
        float w = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        float h = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        if (w <= 0 || h <= 0) return 0;
        return w * h / Math.Min(a.Width * a.Height, b.Width * b.Height);
    }
}
