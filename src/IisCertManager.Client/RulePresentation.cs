using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using IisCertManager.Contracts;
namespace IisCertManager.Client;
public sealed class RulePresentation : IValueConverter
{
    public Settings Settings { get; set; } = new();
    static readonly Brush Green = Frozen("#15803D"), Amber = Frozen("#B45309"), Red = Frozen("#B91C1C"), Grey = Frozen("#64748B"),
        LightGreen = Frozen("#F0FDF4"), LightAmber = Frozen("#FFFBEB"), LightRed = Frozen("#FEF2F2"), LightGrey = Frozen("#F1F5F9");
    static Brush Frozen(string color) { var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!; brush.Freeze(); return brush; }
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not Profile p) return "—";
        var now = DateTimeOffset.Now;
        var days = p.Expires is { } expiry ? (expiry - now).TotalDays : (double?)null;
        var due = days <= Settings.RenewBeforeDays;
        var status = !p.Enabled ? "已暂停" : p.Failures > 0 ? "等待重试" : p.Expires == null ? "等待签发" : days <= 0 ? "已过期" : due ? "待续期" : "证书有效";
        var at = Policy.ScheduledAttempt(p, Settings);
        var schedule = !p.Enabled ? "已暂停" : Settings.Staging ? "测试模式" : at == null || at <= now ? "等待执行" : at.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", culture);
        return (parameter as string) switch
        {
            "mode" => p.Mode switch { ChallengeMode.Auto => "自动选择", ChallengeMode.Http01 => "HTTP-01", ChallengeMode.Dns01 => "DNS-01", _ => "—" },
            "enabled" => p.Enabled ? "已启用" : "已暂停",
            "enabledColor" => p.Enabled ? Green : Grey,
            "status" => status,
            "color" => !p.Enabled ? Grey : p.Failures > 0 || days <= 0 ? Red : due || days == null ? Amber : Green,
            "background" => !p.Enabled ? LightGrey : p.Failures > 0 || days <= 0 ? LightRed : due || days == null ? LightAmber : LightGreen,
            "remaining" => days == null ? "尚未签发证书" : days <= 0 ? "证书已过期" : $"剩余 {Math.Ceiling(days.Value):0} 天",
            "expiry" => p.Expires is { } end ? $"到期 {end.ToLocalTime():yyyy-MM-dd}" : "签发后自动配置 HTTPS",
            "buffer" => days == null ? 0d : Math.Clamp(days.Value / Settings.RenewBeforeDays * 100, 0, 100),
            "bufferHint" => $"剩余有效期余量；少于 {Settings.RenewBeforeDays} 天时进入续期窗口。",
            "schedule" => schedule,
            "scheduleHint" => !p.Enabled ? "自动续期已暂停" : Settings.Staging ? "测试模式不执行自动签发" : p.Failures > 0 ? "失败重试 · 本地时间" : p.Expires == null ? "等待首次签发" : "计划续期 · 本地时间",
            _ => "—"
        };
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
