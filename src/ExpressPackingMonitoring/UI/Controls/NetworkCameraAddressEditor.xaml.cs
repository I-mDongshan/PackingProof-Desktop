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
    public void Load(string? url)
    {
        // 用户已经改过就不再覆盖：设置页切换摄像头再切回来时不能把刚填的内容冲掉。
        if (_isDirty)
            return;

        _isLoading = true;
        try
        {
            if (NetworkCameraAddressTemplate.TryParse(url, out NetworkCameraAddress? address)
                && address != null)
            {
                ApplyAddress(address);
            }
            else
            {
                SelectOption(BrandComboBox, NetworkCameraBrand.Other);
                ManualUrlTextBox.Text = url ?? "";
            }
        }
        finally
        {
            _isLoading = false;
        }

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
        if (_isLoading || !IsLoaded)
            return;

        _isDirty = true;
        UpdateFieldVisibility();
        UpdatePreview();
    }

    private void UpdateFieldVisibility()
    {
        bool manual = IsManualMode;
        Visibility presetVisibility = manual ? Visibility.Collapsed : Visibility.Visible;

        PresetPanel.Visibility = presetVisibility;
        PresetHintText.Visibility = !_compact && presetVisibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;
        PreviewText.Visibility = !_compact && presetVisibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;
        ManualPanel.Visibility = manual ? Visibility.Visible : Visibility.Collapsed;
        DevicePanel.Visibility = presetVisibility == Visibility.Visible
            && SelectedBrand == NetworkCameraBrand.Uniview
                ? Visibility.Visible
                : Visibility.Collapsed;
        ChannelPanel.Visibility = presetVisibility == Visibility.Visible
            && NetworkCameraAddressTemplate.UsesChannel(SelectedBrand, SelectedDevice)
                ? Visibility.Visible
                : Visibility.Collapsed;

        SwitchModeButton.Content = AppLanguage.Get(
            manual ? "改回按品牌填写" : "改为手动填写完整地址");
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

    private void SwitchModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsManualMode)
        {
            string manual = ManualUrlTextBox.Text ?? "";
            if (NetworkCameraAddressTemplate.TryParse(manual, out NetworkCameraAddress? address)
                && address != null)
            {
                _isLoading = true;
                try
                {
                    ApplyAddress(address);
                }
                finally
                {
                    _isLoading = false;
                }
            }
            else
            {
                SelectOption(BrandComboBox, NetworkCameraBrand.Dahua);
            }
        }
        else
        {
            if (TryGetUrl(out string url, out _))
                ManualUrlTextBox.Text = url;
            SelectOption(BrandComboBox, NetworkCameraBrand.Other);
        }

        UpdateFieldVisibility();
        UpdatePreview();
    }

    private static void SelectOption<T>(ComboBox comboBox, T value)
    {
        if (comboBox.ItemsSource is not IEnumerable<Option<T>> options)
            return;

        comboBox.SelectedItem = options.FirstOrDefault(
            option => EqualityComparer<T>.Default.Equals(option.Value, value));
    }
}
