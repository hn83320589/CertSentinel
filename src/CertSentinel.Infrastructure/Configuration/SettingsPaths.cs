namespace CertSentinel.Infrastructure.Configuration;

public static class SettingsPaths
{
    /// <summary>%ProgramData%\CertSentinel — 資料庫與覆寫設定檔的預設位置。</summary>
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CertSentinel");

    /// <summary>覆寫用設定檔;存在時會蓋過安裝目錄的 appsettings.json,升級時不會被覆蓋。</summary>
    public static string ProgramDataSettingsFile { get; } = Path.Combine(DataDirectory, "appsettings.json");
}
