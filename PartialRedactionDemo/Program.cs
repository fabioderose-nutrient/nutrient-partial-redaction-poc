using System.Collections.Concurrent;
using GdPicture14;
using PartialRedactionDemo;

// GdPicture text coordinates are formatted with the current culture: force "." decimals.
System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors();
builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(o =>
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 40 * 1024 * 1024);
var app = builder.Build();

// ---- GdPicture licensing: the key comes from GDPICTURE_LICENSE_KEY only.
// RegisterKEY must always be called: an empty key turns on evaluation mode (watermarked
// output). Without the call, every GdPicture feature throws LicenseMissingFeatureException.
var licenseKey = (Environment.GetEnvironmentVariable("GDPICTURE_LICENSE_KEY") ?? "").Trim();
bool licensed;
string reason = "RegisterKEY returned false";
try { licensed = new LicenseManager().RegisterKEY(licenseKey); }
catch (Exception ex) { licensed = false; reason = ex.Message; }
if (!licensed && licenseKey.Length > 0)
{
    app.Logger.LogWarning("The license key was rejected ({Reason}). Falling back to evaluation mode.", reason);
    licenseKey = "";
    licensed = new LicenseManager().RegisterKEY("");
}
app.Logger.LogInformation("GdPicture {Version} license registered: {Licensed} ({Mode})", RedactionEngine.GdPictureVersion, licensed,
    licenseKey.Length > 0 ? "licensed" : "evaluation mode");

// ---- OCR resources (eng.traineddata), used for scanned pages.
var ocrPath = OcrResources.Resolve();
if (ocrPath is null)
{
    ocrPath = Path.Combine(AppContext.BaseDirectory, "ocr");
    app.Logger.LogWarning("eng.traineddata not found. Set OCR_RESOURCES_PATH (see README). Scanned documents will fail.");
}
else app.Logger.LogInformation("OCR resources: {Path}", ocrPath);

// Samples are generated with GdPicture on first start. In evaluation mode they carry the
// evaluation watermark, so they are kept apart and regenerated once a license key is set.
var samplesDir = Path.Combine(AppContext.BaseDirectory, "Samples", licenseKey.Length > 0 ? "licensed" : "evaluation");
var jobsDir = Path.Combine(Path.GetTempPath(), "partial-redaction-demo");
Directory.CreateDirectory(jobsDir);
SampleFactory.EnsureAll(samplesDir);

var engine = new RedactionEngine(ocrPath);
var jobs = new ConcurrentDictionary<string, JobInfo>();
const float RenderDpi = 144;

app.UseCors(o => o.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/info", () => new
{
    engine = RedactionEngine.EngineLabel,
    gdPictureVersion = RedactionEngine.GdPictureVersion,
    nativePartialRedaction = RedactionEngine.NativePartialOption is not null,
    types = PiiTypes.All.Select(t => new { id = t.Id, label = t.Label, rule = t.PartialRule }),
    samples = SampleFactory.All,
});

app.MapGet("/api/samples/{id}.pdf", (string id) =>
{
    var path = SampleFactory.PathFor(samplesDir, id);
    return SampleFactory.All.Any(s => s.Id == id) && File.Exists(path)
        ? Results.File(path, "application/pdf", id + ".pdf")
        : Results.NotFound();
});

app.MapPost("/api/redact", async (HttpRequest request) =>
{
    var form = await request.ReadFormAsync();
    var types = form["types"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(PiiTypes.FromId).Where(t => t is not null).Select(t => t!.Type).Distinct().ToList();
    if (types.Count == 0) return Results.BadRequest(new { error = "Select at least one data type." });

    byte[] source;
    string name;
    var sampleId = form["sample"].ToString();
    if (!string.IsNullOrEmpty(sampleId))
    {
        var sample = SampleFactory.All.FirstOrDefault(s => s.Id == sampleId);
        if (sample is null) return Results.BadRequest(new { error = "Unknown sample." });
        source = await File.ReadAllBytesAsync(SampleFactory.PathFor(samplesDir, sample.Id));
        name = sample.Title;
    }
    else if (form.Files.Count > 0)
    {
        var file = form.Files[0];
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        name = Path.GetFileNameWithoutExtension(file.FileName);
        try { source = InputConverter.ToPdf(ms.ToArray(), file.FileName); }
        catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
    }
    else return Results.BadRequest(new { error = "Choose a sample or upload a file." });

    RedactionResult result;
    try { result = await Task.Run(() => engine.Process(source, types)); }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Redaction failed");
        return Results.BadRequest(new { error = ex.Message });
    }

    var id = Guid.NewGuid().ToString("N")[..12];
    var dir = Path.Combine(jobsDir, id);
    Directory.CreateDirectory(dir);
    var variants = new (string Key, byte[] Bytes)[] { ("original", result.Original), ("full", result.Full), ("partial", result.Partial) };
    await Task.Run(() => Parallel.ForEach(variants, v =>
    {
        File.WriteAllBytes(Path.Combine(dir, v.Key + ".pdf"), v.Bytes);
        RedactionEngine.RenderPages(v.Bytes, dir, v.Key, RenderDpi);
    }));
    jobs[id] = new JobInfo(dir, name, DateTime.UtcNow);
    CleanupOldJobs(jobs);

    return Results.Ok(new
    {
        jobId = id,
        name,
        engine = result.Engine,
        timings = new { detectMs = result.DetectMs, fullMs = result.FullMs, partialMs = result.PartialMs },
        ocrUsed = result.OcrUsed,
        pages = result.Pages,
        findings = result.Findings,
    });
}).DisableAntiforgery();

app.MapGet("/api/jobs/{id}/{variant}.pdf", (string id, string variant) =>
{
    if (!jobs.TryGetValue(id, out var job) || variant is not ("original" or "full" or "partial")) return Results.NotFound();
    var download = $"{Slug(job.Name)}-{variant}.pdf";
    return Results.File(Path.Combine(job.Dir, variant + ".pdf"), "application/pdf", download);
});

app.MapGet("/api/jobs/{id}/{variant}/{page:int}.png", (string id, string variant, int page) =>
{
    if (!jobs.TryGetValue(id, out var job) || variant is not ("original" or "full" or "partial")) return Results.NotFound();
    var path = Path.Combine(job.Dir, $"{variant}-{page}.png");
    return File.Exists(path) ? Results.File(path, "image/png") : Results.NotFound();
});

app.Run();

static string Slug(string s) =>
    new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');

static void CleanupOldJobs(ConcurrentDictionary<string, JobInfo> jobs)
{
    foreach (var (key, job) in jobs.Where(j => DateTime.UtcNow - j.Value.Created > TimeSpan.FromHours(3)).ToList())
    {
        jobs.TryRemove(key, out _);
        try { Directory.Delete(job.Dir, true); } catch { /* best effort */ }
    }
}

record JobInfo(string Dir, string Name, DateTime Created);
