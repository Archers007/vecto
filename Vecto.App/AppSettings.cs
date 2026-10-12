using System.IO;
using System.Text.Json;
using Vecto.Core;

namespace Vecto.App;

/// <summary>Last-used options, persisted to %APPDATA%\Vecto\settings.json between runs.</summary>
public sealed class AppSettings
{
    public bool AutoColors { get; set; } = true;
    public int ColorCount { get; set; } = 12;
    public int StyleIndex { get; set; }
    public int DetailIndex { get; set; } = 1;
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 780;
    public bool WindowMaximized { get; set; }

    /// <summary>A `portable.txt` beside the exe flips settings to live there too (the
    /// portable ZIP ships it); otherwise %APPDATA%\Vecto as usual.</summary>
    static string FilePath =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt"))
            ? Path.Combine(AppContext.BaseDirectory, "settings.json")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vecto", "settings.json");

    public static AppSettings Load()
    {
        AppSettings settings;
        try
        {
            settings = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new()
                : new();
        }
        catch
        {
            // corrupt or unreadable settings must never block startup
            return new();
        }
        settings.Sanitize();
        return settings;
    }

    /// <summary>Clamps every field to a valid range so a corrupt settings file can never
    /// break startup (e.g. a negative window size throwing in the MainWindow constructor).</summary>
    void Sanitize()
    {
        ColorCount = Math.Clamp(ColorCount, 2, 32);                       // matches the UI slider
        if (!Enum.IsDefined(typeof(ImageStyle), StyleIndex)) StyleIndex = 0;
        if (!Enum.IsDefined(typeof(DetailLevel), DetailIndex)) DetailIndex = 1;
        if (double.IsNaN(WindowWidth) || double.IsInfinity(WindowWidth)) WindowWidth = 1280;
        if (double.IsNaN(WindowHeight) || double.IsInfinity(WindowHeight)) WindowHeight = 780;
        WindowWidth = Math.Clamp(WindowWidth, 320, 16384);
        WindowHeight = Math.Clamp(WindowHeight, 240, 16384);
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // best-effort; a read-only profile must not crash shutdown
        }
    }
}
