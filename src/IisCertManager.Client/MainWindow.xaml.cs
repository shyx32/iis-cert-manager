using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using IisCertManager.Contracts;
namespace IisCertManager.Client;
public partial class MainWindow : Window
{
    Snapshot? snapshot;
    Guid? editingId;
    bool busy;
    public MainWindow() => InitializeComponent();
    async void OnLoaded(object sender, RoutedEventArgs e) => await Refresh(true);
    async void OnRefresh(object sender, RoutedEventArgs e) => await Refresh(true);
    async Task<Response?> Call(Request request, bool loadSettings = false)
    {
        if (busy) return null;
        busy = true; Tabs.IsEnabled = false; Progress.Visibility = Visibility.Visible;
        Status.Text = request.Action == "issue" ? "正在验证域名并签发证书，可能需要数分钟。后台服务将继续执行，请勿重复提交。" : "正在与本机服务通信…";
        try
        {
            var response = await PipeClient.Send(request);
            if (response.Snapshot != null) Apply(response.Snapshot, loadSettings);
            Status.Text = response.Message;
            if (!response.Ok) MessageBox.Show(this, response.Message, "操作未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
            return response;
        }
        catch (TimeoutException) { Status.Text = "无法连接服务，请运行 Install.ps1 并确认 IisCertManager 服务已启动。"; }
        catch (Exception e) { Status.Text = "服务连接失败：" + e.Message + "；请检查服务状态，正在进行的签发可能仍会继续。"; }
        finally { busy = false; Tabs.IsEnabled = true; Progress.Visibility = Visibility.Collapsed; }
        return null;
    }
    void Apply(Snapshot data, bool loadSettings)
    {
        snapshot = data;
        var selected = Sites.SelectedItem as SiteBinding;
        Sites.ItemsSource = data.Bindings;
        if (selected != null) Sites.SelectedItem = data.Bindings.FirstOrDefault(x => x.SiteId == selected.SiteId && x.BindingInformation == selected.BindingInformation && x.Protocol == selected.Protocol);
        Profiles.ItemsSource = data.Profiles;
        Logs.Text = string.Join(Environment.NewLine, data.Logs); Logs.ScrollToEnd();
        DnsStatus.Text = data.Settings.HasDnsCredentials ? "已保存阿里云 DNS 密钥（不会回传到客户端）" : "未配置：自动模式使用 HTTP-01";
        if (!loadSettings) return;
        Email.Text = data.Settings.Email; Staging.IsChecked = data.Settings.Staging;
        Terms.IsChecked = data.Settings.AcceptTerms; RenewDays.Text = data.Settings.RenewBeforeDays.ToString();
        Zone.Text = data.Settings.AliyunZone; KeyId.Clear(); KeySecret.Clear(); ClearDns.IsChecked = false;
    }
    async Task Refresh(bool settings = false) => await Call(new("snapshot"), settings);
    void OnSiteSelected(object sender, SelectionChangedEventArgs e)
    {
        if (Sites.SelectedItem is not SiteBinding row) return;
        Selection.Text = $"{row.SiteName} · {row.Protocol}://{(row.Host.Length == 0 ? "（空主机名）" : row.Host)}:{row.Port}";
        Thumbprint.Text = row.Thumbprint ?? "尚未绑定证书";
        var p = snapshot?.Profiles.FirstOrDefault(x => x.SiteId == row.SiteId && x.Host.Equals(row.Host, StringComparison.OrdinalIgnoreCase) && x.Ip == row.Ip && (row.Protocol != "https" || x.Port == row.Port));
        editingId = p?.Id;
        Domains.Text = p == null ? row.Host : string.Join(", ", p.Domains);
        Mode.SelectedIndex = (int)(p?.Mode ?? ChallengeMode.Auto);
        HttpsPort.Text = (p?.Port ?? (row.Protocol == "https" ? row.Port : 443)).ToString();
        AutoRenew.IsChecked = p?.Enabled ?? true;
    }
    void OnProfileSelected(object sender, SelectionChangedEventArgs e) { }
    async void OnSaveSettings(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(RenewDays.Text, out var days)) { Status.Text = "续期提前天数必须为整数。"; return; }
        await Call(new("settings", Settings: new Settings { Email = Email.Text.Trim(), AcceptTerms = Terms.IsChecked == true,
            Staging = Staging.IsChecked == true, RenewBeforeDays = days, AliyunZone = Zone.Text.Trim(),
            AccessKeyId = KeyId.Password.Trim(), AccessKeySecret = KeySecret.Password.Trim(), ClearDnsCredentials = ClearDns.IsChecked == true }), true);
    }
    Profile? MakeProfile()
    {
        if (Sites.SelectedItem is not SiteBinding row) { Status.Text = "请先选择 IIS 绑定。"; return null; }
        if (!int.TryParse(HttpsPort.Text, out var port)) { Status.Text = "HTTPS 端口必须为整数。"; return null; }
        return new Profile { Id = editingId ?? Guid.NewGuid(), SiteId = row.SiteId, SiteName = row.SiteName,
            Host = row.Host, Ip = row.Ip, Port = port,
            Domains = Domains.Text.Split(new[] { ',', '，', ';', ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries),
            Mode = (ChallengeMode)Mode.SelectedIndex, Enabled = AutoRenew.IsChecked == true };
    }
    async Task<Guid?> SaveProfile()
    {
        var profile = MakeProfile(); if (profile == null) return null;
        var response = await Call(new("profile", Profile: profile));
        if (response?.Ok != true) return null;
        editingId = profile.Id; return profile.Id;
    }
    async void OnSaveProfile(object sender, RoutedEventArgs e) => await SaveProfile();
    async void OnIssue(object sender, RoutedEventArgs e)
    {
        var id = await SaveProfile(); if (id != null) await Call(new("issue", Id: id));
    }
    async void OnIssueManaged(object sender, RoutedEventArgs e)
    {
        if (Profiles.SelectedItem is Profile p) await Call(new("issue", Id: p.Id));
        else Status.Text = "请选择托管规则。";
    }
    void OnEditProfile(object sender, RoutedEventArgs e)
    {
        if (Profiles.SelectedItem is not Profile p || snapshot == null) return;
        var binding = snapshot.Bindings.FirstOrDefault(x => x.SiteId == p.SiteId && x.Host.Equals(p.Host, StringComparison.OrdinalIgnoreCase) && x.Ip == p.Ip);
        if (binding == null) { Status.Text = "原 IIS 绑定不存在，请先检查 IIS。"; return; }
        Sites.SelectedItem = binding; editingId = p.Id;
        Domains.Text = string.Join(", ", p.Domains); Mode.SelectedIndex = (int)p.Mode;
        HttpsPort.Text = p.Port.ToString(); AutoRenew.IsChecked = p.Enabled; Tabs.SelectedIndex = 0;
    }
    async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (Profiles.SelectedItem is Profile p)
        { var result = await Call(new("delete", Id: p.Id)); if (result?.Ok == true && editingId == p.Id) editingId = null; }
    }
    void OnNavigate(object sender, RequestNavigateEventArgs e)
    { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); e.Handled = true; }
}
