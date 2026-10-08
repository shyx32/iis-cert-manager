using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using System.Windows.Data;
using System.Windows.Media;
using IisCertManager.Contracts;
namespace IisCertManager.Client;
public partial class MainWindow : Window
{
    Snapshot? snapshot;
    Guid? editingId;
    bool applying, closed;
    int foregroundCalls;
    readonly SemaphoreSlim requests = new(1, 1);
    bool settingsLoaded;
    public MainWindow() => InitializeComponent();
    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await Refresh();
    }
    void OnClosed(object? sender, EventArgs e) { closed = true; }
    async void OnRefresh(object sender, RoutedEventArgs e) => await Refresh();
    async Task<Response?> Call(Request request, bool loadSettings = false)
    {
        if (closed) return null;
        foregroundCalls++; Tabs.IsEnabled = false; RefreshButton.IsEnabled = false;
        await requests.WaitAsync();
        if (closed)
        {
            foregroundCalls--; requests.Release(); return null;
        }
        Progress.Visibility = Visibility.Visible;
        Status.Text = request.Action == "issue" ? "正在验证域名并签发证书，可能需要数分钟。后台服务将继续执行，请勿重复提交。" : "正在与本机服务通信…";
        try
        {
            var response = await PipeClient.Send(request);
            if (response.Snapshot != null) Apply(response.Snapshot, loadSettings || !settingsLoaded);
            Status.Text = response.Ok ? request.Action switch
            {
                "snapshot" => "已读取最新 IIS 配置。",
                "settings" => "签发与续期设置已保存。",
                "profile" => "托管规则已保存。",
                "issue" => snapshot?.Settings.Staging == true ? "测试签发完成，详情见操作日志。" : "证书已签发，IIS HTTPS 绑定已更新并核验。",
                _ => response.Message
            } : response.Message;
            if (!response.Ok) MessageBox.Show(this, response.Message, "操作未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
            return response;
        }
        catch (TimeoutException) { SyncStatus.Text = "同步失败"; Status.Text = "无法连接服务。请重新运行安装器，或检查 IIS Certificate Manager 服务是否已启动。"; }
        catch (Exception e) { SyncStatus.Text = "同步失败"; Status.Text = "服务连接失败：" + e.Message + "；请检查服务状态，正在进行的签发可能仍会继续。"; }
        finally
        {
            foregroundCalls--;
            Progress.Visibility = foregroundCalls == 0 ? Visibility.Collapsed : Visibility.Visible;
            Tabs.IsEnabled = foregroundCalls == 0; RefreshButton.IsEnabled = foregroundCalls == 0;
            requests.Release();
        }
        return null;
    }
    void Apply(Snapshot data, bool loadSettings)
    {
        snapshot = data;
        ((BindingPresentation)Resources["BindingView"]).RenewBeforeDays = data.Settings.RenewBeforeDays;
        SetupNotice.Visibility = data.Settings.AcceptTerms && data.Settings.Email.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyProfiles.Visibility = data.Profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var selected = Sites.SelectedItem as SiteBinding;
        var managed = (Profiles.SelectedItem as Profile)?.Id;
        applying = true;
        try
        {
            Sites.ItemsSource = data.Bindings;
            FilterBindings();
            Sites.SelectedItem = selected == null ? null : data.Bindings.FirstOrDefault(x => x.SiteId == selected.SiteId && x.BindingInformation == selected.BindingInformation && x.Protocol == selected.Protocol);
            Profiles.ItemsSource = data.Profiles;
            Profiles.SelectedItem = data.Profiles.FirstOrDefault(x => x.Id == managed);
        }
        finally { applying = false; }
        if (Sites.SelectedItem is SiteBinding current) ShowBinding(current);
        else { HideDetails(); }
        if (Sites.SelectedItem == null && selected != null) { editingId = null; Selection.Text = "所选绑定已变更，请重新选择"; BindingDetails.Text = "IIS 配置已刷新，原绑定已不存在。"; RootPath.Text = "—"; CertificateDetails.Text = "—"; Thumbprint.Text = "—"; }
        SiteCount.Text = $"{data.Bindings.Select(x => x.SiteId).Distinct().Count()} / {data.Bindings.Count}";
        var https = data.Bindings.Where(x => x.Protocol == "https").ToArray();
        var expiring = https.Count(x => x.Expires <= DateTimeOffset.Now.AddDays(data.Settings.RenewBeforeDays));
        CertificateCount.Text = $"{https.Length} / {expiring}";
        CertificateCount.Foreground = expiring > 0 ? Brushes.Firebrick : new SolidColorBrush(Color.FromRgb(0x17, 0x2B, 0x45));
        RenewalSummary.Text = $"{data.Profiles.Count(x => x.Enabled)} 条规则 · 提前 {data.Settings.RenewBeforeDays} 天";
        SyncStatus.Text = $"最近同步 {DateTime.Now:HH:mm:ss}";
        var logText = string.Join(Environment.NewLine, data.Logs);
        if (Logs.Text != logText)
        {
            var offset = Logs.VerticalOffset;
            Logs.Text = logText;
            Logs.ScrollToVerticalOffset(offset);
        }
        EmptyLogs.Visibility = data.Logs.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        CopyLogsButton.IsEnabled = data.Logs.Length > 0;
        DnsStatus.Text = data.Settings.HasDnsCredentials ? "已保存阿里云 DNS 密钥（不会回传到客户端）" : "未配置：自动模式使用 HTTP-01";
        if (!loadSettings) return;
        settingsLoaded = true;
        Email.Text = data.Settings.Email; Staging.IsChecked = data.Settings.Staging;
        Terms.IsChecked = data.Settings.AcceptTerms; RenewDays.Text = data.Settings.RenewBeforeDays.ToString();
        Zone.Text = data.Settings.AliyunZone; KeyId.Clear(); KeySecret.Clear(); ClearDns.IsChecked = false;
    }
    async Task Refresh(bool settings = false) => await Call(new("snapshot"), settings);
    void OnSiteSelected(object sender, SelectionChangedEventArgs e)
    {
        if (applying) return;
        if (Sites.SelectedItem is SiteBinding row) LoadBinding(row);
        else HideDetails();
    }
    void ShowBinding(SiteBinding row)
    {
        DetailColumn.Width = new GridLength(356); DetailPanel.Visibility = Visibility.Visible; SetupNotice.Visibility = Visibility.Collapsed;
        IssueButton.IsEnabled = row.Host.Length > 0 && snapshot?.Settings.AcceptTerms == true && snapshot.Settings.Email.Length > 0;
        Selection.Text = row.Host.Length == 0 ? "默认绑定" : row.Host;
        BindingDetails.Text = $"{row.SiteName}\n{row.Protocol.ToUpperInvariant()} · {row.Ip}:{row.Port} · {(row.State == "Started" ? "运行中" : row.State == "Stopped" ? "已停止" : row.State)}";
        PoolDetails.Text = row.ApplicationPool ?? "—";
        CertificateSummary.Text = row.Thumbprint == null ? "当前未绑定证书" : $"当前证书到期：{row.Expires?.ToLocalTime().ToString("yyyy-MM-dd") ?? "未知"}";
        RootPath.Text = row.Root;
        CertificateDetails.Text = row.Thumbprint == null ? "未绑定证书" : $"{row.CertificateSubject ?? "证书不可读取"}\n颁发者：{row.CertificateIssuer ?? "—"}\n到期：{row.Expires?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "未知"} · {row.CertificateStore ?? "My"}";
        Thumbprint.Text = row.Thumbprint ?? "—";
    }
    void LoadBinding(SiteBinding row)
    {
        ShowBinding(row);
        var p = snapshot?.Profiles.FirstOrDefault(x => x.SiteId == row.SiteId && x.Host.Equals(row.Host, StringComparison.OrdinalIgnoreCase) && x.Ip == row.Ip && (row.Protocol != "https" || x.Port == row.Port));
        editingId = p?.Id;
        Domains.Text = p == null ? row.Host : string.Join(", ", p.Domains);
        Mode.SelectedIndex = (int)(p?.Mode ?? ChallengeMode.Auto);
        HttpsPort.Text = (p?.Port ?? (row.Protocol == "https" ? row.Port : 443)).ToString();
        AutoRenew.IsChecked = p?.Enabled ?? true;
    }
    async void OnReloadSelection(object sender, RoutedEventArgs e)
    {
        var response = await Call(new("snapshot"));
        if (response?.Ok == true && Sites.SelectedItem is SiteBinding row) { LoadBinding(row); Status.Text = "已用当前 IIS 绑定和已保存规则重新填充配置。"; }
    }
    void OnSearch(object sender, TextChangedEventArgs e) { if (!applying) FilterBindings(); }
    void FilterBindings()
    {
        if (Sites?.ItemsSource == null) return;
        var query = Search.Text.Trim();
        CollectionViewSource.GetDefaultView(Sites.ItemsSource).Filter = item => item is SiteBinding row &&
            (query.Length == 0 || $"{row.SiteName} {row.Host} {row.Ip} {row.Protocol} {row.Port}".Contains(query, StringComparison.OrdinalIgnoreCase));
        BindingSummary.Text = $"{Sites.Items.Count} 个绑定 · 手动同步";
        EmptySites.Text = query.Length == 0 ? "暂无 HTTP 或 HTTPS 绑定，请先在 IIS 中创建站点。" : "没有匹配的绑定，请尝试其他关键词。";
        EmptySites.Visibility = Sites.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
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
        Search.Clear();
        var binding = snapshot.Bindings.FirstOrDefault(x => x.SiteId == p.SiteId && x.Host.Equals(p.Host, StringComparison.OrdinalIgnoreCase) && x.Ip == p.Ip);
        if (binding == null) { Status.Text = "原 IIS 绑定不存在，请先检查 IIS。"; return; }
        Sites.SelectedItem = binding; editingId = p.Id;
        Domains.Text = string.Join(", ", p.Domains); Mode.SelectedIndex = (int)p.Mode;
        HttpsPort.Text = p.Port.ToString(); AutoRenew.IsChecked = p.Enabled; Tabs.SelectedIndex = 0;
    }
    async void OnToggleRenewal(object sender, RoutedEventArgs e)
    {
        if (Profiles.SelectedItem is Profile p) await Call(new("enabled", Id: p.Id, Enabled: !p.Enabled));
        else Status.Text = "请选择托管规则。";
    }
    async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (Profiles.SelectedItem is Profile p)
        { var result = await Call(new("delete", Id: p.Id)); if (result?.Ok == true && editingId == p.Id) editingId = null; }
    }
    void HideDetails()
    {
        DetailPanel.Visibility = Visibility.Collapsed; DetailColumn.Width = new GridLength(0);
        SetupNotice.Visibility = snapshot != null && !(snapshot.Settings.AcceptTerms && snapshot.Settings.Email.Length > 0) ? Visibility.Visible : Visibility.Collapsed;
    }
    void OnCloseDetails(object sender, RoutedEventArgs e) => Sites.SelectedItem = null;
    void OnOpenSettings(object sender, RoutedEventArgs e) => Tabs.SelectedIndex = 2;
    void OnCopyLogs(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(Logs.Text); Status.Text = "日志已复制。"; }
        catch (Exception error) { Status.Text = "无法复制日志：" + error.Message; }
    }
    void OnNavigate(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception error) { Status.Text = "无法打开浏览器：" + error.Message; }
        e.Handled = true;
    }
}
