using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using IisCertManager.Contracts;
namespace IisCertManager.Client;
// Presentation is computed only when a snapshot is applied; no clock or UI timer is needed.
public sealed class BindingPresentation : IValueConverter
{
    public int RenewBeforeDays { get; set; } = 30;
    static readonly Brush Grey = Frozen("#64748B"), Green = Frozen("#15803D"), Red = Frozen("#B91C1C"), Amber = Frozen("#B45309"), LightGrey = Frozen("#F1F5F9"), LightGreen = Frozen("#F0FDF4");
    static Brush Frozen(string value) { var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(value)!; brush.Freeze(); return brush; }
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not SiteBinding row) return "—";
        var https = row.Protocol.Equals("https", StringComparison.OrdinalIgnoreCase);
        var expired = row.Expires <= DateTimeOffset.Now;
        var soon = row.Expires <= DateTimeOffset.Now.AddDays(RenewBeforeDays);
        return (parameter as string) switch
        {
            "host" => row.Host.Length == 0 ? "默认绑定" : row.Host,
            "protocolBackground" => https ? LightGreen : LightGrey,
            "protocolForeground" => https ? Green : Grey,
            "status" => !https ? "未启用 HTTPS" : row.Thumbprint == null ? "未绑定证书" : expired ? "已过期" : row.Expires == null ? "无法读取" : soon ? "即将到期" : "有效",
            "statusForeground" => !https ? Grey : expired || row.Thumbprint == null ? Red : row.Expires == null || soon ? Amber : Green,
            _ => "—"
        };
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
