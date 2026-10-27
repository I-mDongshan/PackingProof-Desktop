using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.Services;

namespace ExpressPackingMonitoring.UI.Controls;

/// <summary>
/// 网络摄像头地址的共用编辑器：选品牌后只填地址/账号/密码，完整 RTSP 地址由
/// <see cref="NetworkCameraAddressTemplate"/> 拼出来；"其他"保留完整地址手动填写。
/// 设置页与首用向导共用这一份，避免两边各维护一套地址规则。
/// </summary>
public partial class NetworkCameraAddressEditor : UserControl
{
    private sealed record Option<T>(string Label, T Value);

    private bool _isLoading;
    private bool _isDirty;
    private bool _compact;
    private bool _isUpdatingUrl;
    private string _lastComposedUrl = "";

    /// <summary>
    /// 拼出来的完整地址（"其他"模式就是用户填的地址）。副画面卡片等场景直接绑定这个属性；
    /// 只有校验通过的地址才会回写，避免把半截输入存进配置。
    /// </summary>
    public static readonly DependencyProperty UrlProperty =
        DependencyProperty.Register(
            nameof(Url),
            typeof(string),
            typeof(NetworkCameraAddressEditor),
            new FrameworkPropertyMetadata(
                "",
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnUrlPropertyChanged));

    public string Url
    {
        get => (string)GetValue(UrlProperty);
        set => SetValue(UrlProperty, value);
    }

    private static void OnUrlPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not NetworkCameraAddressEditor editor || editor._isUpdatingUrl)
            return;

        editor.Load(e.NewValue as string, force: true);
    }

    /// <summary>紧凑模式：隐藏地址预览和品牌提示，给首用向导的画面预览留高度。</summary>
    public bool Compact
    {
        get => _compact;
        set
        {
            _compact = value;
            UpdateFieldVisibility();
        }
    }

    public NetworkCameraAddressEditor()
    {
        InitializeComponent();

        BrandComboBox.ItemsSource = new[]
        {
            new Option<NetworkCameraBrand>(AppLanguage.Get("大华"), NetworkCameraBrand.Dahua),
            new Option<NetworkCameraBrand>(AppLanguage.Get("海康威视"), NetworkCameraBrand.Hikvision),
            new Option<NetworkCameraBrand>(AppLanguage.Get("宇视"), NetworkCameraBrand.Uniview),
            new Option<NetworkCameraBrand>(AppLanguage.Get("其他"), NetworkCameraBrand.Other)
        };
        BrandComboBox.SelectedIndex = 0;

        DeviceComboBox.ItemsSource = new[]
        {
            new Option<NetworkCameraDeviceKind>(
                AppLanguage.Get("摄像机（单路）"),
                NetworkCameraDeviceKind.Camera),
            new Option<NetworkCameraDeviceKind>(
                AppLanguage.Get("录像机（多路）"),
                NetworkCameraDeviceKind.Recorder)
        };
        DeviceComboBox.SelectedIndex = 0;

        StreamComboBox.ItemsSource = new[]
        {
            new Option<NetworkCameraStreamKind>(AppLanguage.Get("主码流"), NetworkCameraStreamKind.Main),
            new Option<NetworkCameraStreamKind>(AppLanguage.Get("子码流"), NetworkCameraStreamKind.Sub)
        };
        StreamComboBox.SelectedIndex = 0;

        PortTextBox.Text = NetworkCameraAddressTemplate.DefaultPort.ToString(CultureInfo.InvariantCulture);
        ChannelTextBox.Text = NetworkCameraAddressTemplate.DefaultChannel.ToString(CultureInfo.InvariantCulture);

        Loaded += (_, _) =>
        {
            UpdateFieldVisibility();
            UpdatePreview();
        };
    }

    /// <summary>把已有地址回填到界面：能识别品牌路径就回填字段，否则落到"其他"显示完整地址。</summary>
    public void Load(string? url, bool force = false)
    {
        // 用户已经改过就不再覆盖：设置页切换摄像头再切回来时不能把刚填的内容冲掉。
        // 绑定源（副画面卡片）换了地址时 force=true，必须按新值重填。
        if (_isDirty && !force)
            return;

        _isLoading = true;
        try
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                // 新配置默认落在第一个品牌上，用户直接看到"地址+账号密码"的简化表单；
                // 已有地址解析不出品牌时才落到"其他"手动填写。
                SelectOption(BrandComboBox, NetworkCameraBrand.Dahua);
            }
            else if (NetworkCameraAddressTemplate.TryParse(url, out NetworkCameraAddress? address)
                && address != null)
            {
                ApplyAddress(address);
            }
            else
            {
                SelectOption(BrandComboBox, NetworkCameraBrand.Other);
                ManualUrlTextBox.Text = url;
            }
        }
        finally
        {
            _isLoading = false;
        }

        _isDirty = false;
        UpdateFieldVisibility();
        UpdatePreview();
    }

    /// <summary>拼出最终地址并校验；错误信息可直接展示（资源里有对应译文）。</summary>
    public bool TryGetUrl(out string url, out string error) =>
        IsManualMode
            ? NetworkCameraUrlPolicy.TryNormalize(ManualUrlTextBox.Text, out url, out error)
            : NetworkCameraAddressTemplate.TryCompose(
                SelectedBrand,
                SelectedDevice,
                HostTextBox.Text,
                PortTextBox.Text,
                UserTextBox.Text,
                PasswordTextBox.Text,
                SelectedStream,
                ChannelTextBox.Text,
                out url,
                out error);

    private bool IsManualMode => SelectedBrand == NetworkCameraBrand.Other;

    private NetworkCameraBrand SelectedBrand =>
        (BrandComboBox.SelectedItem as Option<NetworkCameraBrand>)?.Value ?? NetworkCameraBrand.Other;

    private NetworkCameraDeviceKind SelectedDevice =>
        (DeviceComboBox.SelectedItem as Option<NetworkCameraDeviceKind>)?.Value
        ?? NetworkCameraDeviceKind.Camera;

    private NetworkCameraStreamKind SelectedStream =>
        (StreamComboBox.SelectedItem as Option<NetworkCameraStreamKind>)?.Value
        ?? NetworkCameraStreamKind.Main;

    private void ApplyAddress(NetworkCameraAddress address)
    {
        SelectOption(BrandComboBox, address.Brand);
        SelectOption(DeviceComboBox, address.Device);
        SelectOption(StreamComboBox, address.Stream);
        HostTextBox.Text = address.Host;
        PortTextBox.Text = address.Port.ToString(CultureInfo.InvariantCulture);
        UserTextBox.Text = address.UserName;
        PasswordTextBox.Text = address.Password;
        ChannelTextBox.Text = address.Channel.ToString(CultureInfo.InvariantCulture);
    }

    private void Text_Changed(object sender, TextChangedEventArgs e) => OnFieldChanged();

    private void Selection_Changed(object sender, SelectionChangedEventArgs e) => OnFieldChanged();

    private void OnFieldChanged()
    {
        if (_isLoading)
            return;

        if (IsLoaded)
        {
            // 构造阶段（还没进可视树）由代码赋默认值不算用户改动，否则随后 Load 会被 dirty 拦住。
            _isDirty = true;
            UpdateFieldVisibility();
            UpdatePreview();
        }

        PublishUrl();
    }

    /// <summary>把校验通过的地址回写给绑定方；半截输入不覆盖配置里的旧值。</summary>
    private void PublishUrl()
    {
        if (_isLoading || _isUpdatingUrl)
            return;
        if (!TryGetUrl(out string url, out _) || string.Equals(Url, url, StringComparison.Ordinal))
            return;

        _isUpdatingUrl = true;
        try
        {
            SetCurrentValue(UrlProperty, url);
            GetBindingExpression(UrlProperty)?.UpdateSource();
        }
        finally
        {
            _isUpdatingUrl = false;
        }
    }

    private void UpdateFieldVisibility()
    {
        bool manual = IsManualMode;
        if (!manual && TryGetUrl(out string composed, out _))
            _lastComposedUrl = composed;

        Visibility presetVisibility = manual ? Visibility.Collapsed : Visibility.Visible;
        HostLabel.Visibility = presetVisibility;
        HostTextBox.Visibility = presetVisibility;
        PortLabel.Visibility = presetVisibility;
        PortTextBox.Visibility = presetVisibility;
        UserLabel.Visibility = presetVisibility;
        UserTextBox.Visibility = presetVisibility;
        PasswordLabel.Visibility = presetVisibility;
        PasswordTextBox.Visibility = presetVisibility;
        StreamLabel.Visibility = presetVisibility;
        StreamComboBox.Visibility = presetVisibility;

        // 品牌下拉常驻；选"其他"时右侧直接变成完整地址。
        ManualLabel.Visibility = manual ? Visibility.Visible : Visibility.Collapsed;
        ManualUrlTextBox.Visibility = manual ? Visibility.Visible : Visibility.Collapsed;
        ManualHintText.Visibility = manual ? Visibility.Visible : Visibility.Collapsed;

        bool showDevice = !manual && SelectedBrand == NetworkCameraBrand.Uniview;
        DeviceLabel.Visibility = showDevice ? Visibility.Visible : Visibility.Collapsed;
        DeviceComboBox.Visibility = showDevice ? Visibility.Visible : Visibility.Collapsed;

        // 设备类型只对宇视显示；隐藏时码流/通道左移补位，避免第三行开头空一格。
        int streamColumn = showDevice ? 3 : 0;
        Grid.SetColumn(StreamLabel, streamColumn);
        Grid.SetColumn(StreamComboBox, streamColumn + 1);
        Grid.SetColumn(ChannelLabel, streamColumn + 3);
        Grid.SetColumn(ChannelTextBox, streamColumn + 4);

        bool showChannel = !manual
            && NetworkCameraAddressTemplate.UsesChannel(SelectedBrand, SelectedDevice);
        ChannelLabel.Visibility = showChannel ? Visibility.Visible : Visibility.Collapsed;
        ChannelTextBox.Visibility = showChannel ? Visibility.Visible : Visibility.Collapsed;

        PresetHintText.Visibility = !manual && !_compact
            ? Visibility.Visible
            : Visibility.Collapsed;
        PreviewText.Visibility = !manual && !_compact
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdatePreview()
    {
        if (IsManualMode)
        {
            PreviewText.Text = "";
            return;
        }

        string password = PasswordTextBox.Text.Length > 0 ? "***" : "";
        if (!NetworkCameraAddressTemplate.TryCompose(
                SelectedBrand,
                SelectedDevice,
                HostTextBox.Text,
                PortTextBox.Text,
                UserTextBox.Text,
                password,
                SelectedStream,
                ChannelTextBox.Text,
                out string preview,
                out _))
        {
            PreviewText.Text = "";
            return;
        }

        PreviewText.Text = $"{AppLanguage.Get("完整拉流地址")}：{preview}";
    }

    /// <summary>
    /// 品牌切到"其他"时，把刚刚拼出的地址带进完整地址框，省得用户重打；
    /// 切回品牌时字段保留，手动框里的内容也留着，再选"其他"能继续改。
    /// </summary>
    private void BrandSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoading
            && IsManualMode
            && string.IsNullOrWhiteSpace(ManualUrlTextBox.Text)
            && _lastComposedUrl.Length > 0)
        {
            _isLoading = true;
            try
            {
                ManualUrlTextBox.Text = _lastComposedUrl;
            }
            finally
            {
                _isLoading = false;
            }
        }

        OnFieldChanged();
    }

    private static void SelectOption<T>(ComboBox comboBox, T value)
    {
        if (comboBox.ItemsSource is not IEnumerable<Option<T>> options)
            return;

        comboBox.SelectedItem = options.FirstOrDefault(
            option => EqualityComparer<T>.Default.Equals(option.Value, value));
    }
}
