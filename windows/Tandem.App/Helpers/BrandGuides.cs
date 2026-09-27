namespace Tandem.App;

/// <summary>
/// Where things live on each brand's Settings app. Wording matches what's on screen, so the
/// guide can say "tap OS version" rather than a generic "tap Build number".
/// </summary>
public sealed record BrandGuide(
    string Id,
    string Name,
    string Examples,
    string AboutPath,
    string TapTarget,
    string DevOptionsPath,
    string[] AboutRows,
    string? Note = null)
{
    public static readonly BrandGuide Other = new(
        "other", "Another Android phone", "Motorola, Nokia, Nothing, Sony…",
        "Settings → About phone", "Build number", "Settings → System → Developer options",
        ["Device name", "Model", "Android version", "Build number"],
        "The names can differ a little between phones. The search bar at the top of Settings finds them.");

    public static readonly IReadOnlyList<BrandGuide> All =
    [
        new("samsung", "Samsung", "Galaxy S, A, M, Z…",
            "Settings → About phone → Software information", "Build number", "Settings → Developer options",
            ["One UI version", "Android version", "Baseband version", "Kernel version", "Build number"]),
        new("xiaomi", "Xiaomi, Redmi or POCO", "Redmi Note, POCO F, Xiaomi Pad…",
            "Settings → About phone", "OS version", "Settings → Additional settings → Developer options",
            ["Device name", "Processor", "RAM", "OS version"],
            "On older phones it's called MIUI version."),
        new("pixel", "Google Pixel", "Pixel 6, 7, 8, 9…",
            "Settings → About phone", "Build number", "Settings → System → Developer options",
            ["Device name", "Phone number", "Android version", "Build number"]),
        new("oneplus", "OnePlus, OPPO or realme", "OnePlus Nord, OPPO Reno, realme…",
            "Settings → About device → Version", "Build number", "Settings → Additional settings → Developer options",
            ["Android version", "Baseband version", "Kernel version", "Build number"]),
        new("vivo", "vivo or iQOO", "vivo V, Y, X series, iQOO…",
            "Settings → About phone → Software version", "Software version", "Settings → System management → Developer options",
            ["Android version", "Software version"]),
        Other,
    ];

    public static BrandGuide ById(string? id) => All.FirstOrDefault(b => b.Id == id) ?? Other;

    /// <summary>Guess the guide from a connected phone (ro.product.manufacturer).</summary>
    public static BrandGuide ForManufacturer(string manufacturer) => manufacturer.ToLowerInvariant() switch
    {
        "samsung" => ById("samsung"),
        "xiaomi" or "redmi" or "poco" => ById("xiaomi"),
        "google" => ById("pixel"),
        "oneplus" or "oppo" or "realme" => ById("oneplus"),
        "vivo" or "iqoo" => ById("vivo"),
        _ => Other,
    };
}
