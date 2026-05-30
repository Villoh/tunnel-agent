using CommunityToolkit.Mvvm.ComponentModel;
using IconPacks.Avalonia.SimpleIcons;

namespace TunnelAgent.ViewModels;

public partial class QuotaProviderViewModel : ViewModelBase
{
    public string Id          { get; }
    public string Name        { get; }
    public PackIconSimpleIconsKind IconKind    { get; }
    public string                  LogoColor   { get; }
    public string                  Description { get; }
    public string?                 CustomIconData { get; }
    public bool                    HasCustomIcon  => CustomIconData is not null;

    [ObservableProperty] private string  _email      = "";
    [ObservableProperty] private bool    _isDetected;
    [ObservableProperty] private string  _planType   = "";
    [ObservableProperty] private bool    _isScanning;
    [ObservableProperty] private bool    _maskEmails;

    partial void OnMaskEmailsChanged(bool value) => OnPropertyChanged(nameof(DisplayEmail));
    partial void OnEmailChanged(string value)     => OnPropertyChanged(nameof(DisplayEmail));

    public string DisplayEmail =>
        string.IsNullOrEmpty(Email) ? Email :
        MaskEmails ? MaskEmailAddress(Email) : Email;

    private static string MaskEmailAddress(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 0) return email;
        var maskedLocal  = new string('•', at);
        var maskedDomain = new string('•', email.Length - at - 1);
        return $"{maskedLocal}@{maskedDomain}";
    }

    public QuotaProviderViewModel(string id, string name,
        PackIconSimpleIconsKind iconKind, string logoColor, string description,
        string? customIconData = null)
    {
        Id             = id;
        Name           = name;
        IconKind       = iconKind;
        LogoColor      = logoColor;
        Description    = description;
        CustomIconData = customIconData;
    }
}
