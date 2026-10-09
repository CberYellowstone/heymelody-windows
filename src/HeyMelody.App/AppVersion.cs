namespace HeyMelodyWindows.App;

public static class AppVersion
{
    public static string Display { get; } = typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    public static string Footer => $"{Display} · 社区版";
}
