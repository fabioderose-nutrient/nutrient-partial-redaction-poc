namespace PartialRedactionDemo;

/// <summary>
/// Finds the folder that holds the OCR resources (eng.traineddata) used for scanned pages.
/// Order: OCR_RESOURCES_PATH, an "ocr" folder next to the app or in the working directory,
/// then the GdPicture.Resources package in the local NuGet cache.
/// </summary>
public static class OcrResources
{
    private const string Dictionary = "eng.traineddata";

    public static string? Resolve()
    {
        var candidates = new List<string>();
        var env = Environment.GetEnvironmentVariable("OCR_RESOURCES_PATH");
        if (!string.IsNullOrWhiteSpace(env)) candidates.Add(Path.GetFullPath(env));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "ocr"));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "ocr"));

        var nugetRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } np
            ? np
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        var package = Path.Combine(nugetRoot, "gdpicture.resources");
        if (Directory.Exists(package))
            foreach (var version in Directory.GetDirectories(package).OrderByDescending(v => v))
                candidates.Add(Path.Combine(version, "runtimes", "any", "native"));

        return candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, Dictionary)));
    }
}
