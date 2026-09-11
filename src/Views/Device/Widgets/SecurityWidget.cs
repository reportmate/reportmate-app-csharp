using System.Windows;
using System.Windows.Controls;
using ReportMate.App.Services;
using ReportMate.App.Views.Shared;

namespace ReportMate.App.Views.Device.Widgets;

/// <summary>Windows protection status: antivirus, BitLocker, Windows Hello and firewall.</summary>
public static class SecurityWidget
{
    public static UIElement Build(DeviceSnapshot s)
    {
        var sec = s.Security;
        if (sec is null)
            return Ui.StatBlock("Security", "Protection status", "", Accent.Red, Ui.EmptyState("Security information not available"));

        var body = new StackPanel();

        // Antivirus
        var av = sec.Antivirus;
        var avStatus = av is null ? "Unknown" : av.IsEnabled && av.IsUpToDate ? "Current" : av.IsEnabled ? "Enabled" : "Disabled";
        body.Children.Add(Ui.StatusBadge("Antivirus", DeviceSnapshot.FirstNonEmpty(av?.StatusDisplay) ?? avStatus, Ui.ToneFor(av?.IsEnabled, avStatus)));
        if (av is not null)
        {
            var detail = new StackPanel { Margin = new Thickness(16, 2, 0, 8) };
            detail.Children.Add(Ui.Text(Format.OrUnknown(av.Name)));
            if (!string.IsNullOrWhiteSpace(av.Version)) detail.Children.Add(Ui.Caption($"Version: {av.Version}"));
            if (av.LastUpdate is not null) detail.Children.Add(Ui.Caption($"Updated: {Format.ShortDateTime(av.LastUpdate)}"));
            if (av.LastScan is not null)
                detail.Children.Add(Ui.Caption($"Last Scan: {Format.ShortDateTime(av.LastScan)}" + (string.IsNullOrWhiteSpace(av.ScanType) ? "" : $" ({av.ScanType})")));
            body.Children.Add(detail);
        }

        // BitLocker
        var bl = sec.Encryption?.BitLocker;
        var blStatus = DeviceSnapshot.FirstNonEmpty(sec.Encryption?.StatusDisplay, bl?.Status) ?? (bl?.IsEnabled == true ? "Enabled" : "Disabled");
        body.Children.Add(Ui.StatusBadge("BitLocker", blStatus, bl?.IsEnabled == true ? Tone.Success : Tone.Error));

        // Windows Hello lives in the identity module on Windows.
        var hello = s.Identity?.WindowsHello;
        if (hello?.CredentialProviders is { } cp)
        {
            var label = Ui.Label("Windows Hello");
            label.Margin = new Thickness(0, 8, 0, 4);
            body.Children.Add(label);
            var inner = new StackPanel { Margin = new Thickness(16, 0, 0, 4) };
            inner.Children.Add(Ui.StatusBadge("PIN Status", Format.EnabledDisabled(cp.PinEnabled), Ui.ToneFor(cp.PinEnabled)));
            var bio = cp.FaceRecognitionEnabled || cp.FingerprintEnabled;
            inner.Children.Add(Ui.StatusBadge("Biometric Status", Format.EnabledDisabled(bio), Ui.ToneFor(bio)));
            body.Children.Add(inner);
        }

        // Firewall
        var fw = sec.Firewall;
        var fwStatus = DeviceSnapshot.FirstNonEmpty(fw?.StatusDisplay) ?? (fw is null ? "Unknown" : fw.IsEnabled ? "Enabled" : "Disabled");
        body.Children.Add(Ui.StatusBadge("Firewall", fwStatus, fw?.IsEnabled == true ? Tone.Success : Tone.Warning));

        // Secure Boot and the TPM. The web's summary carries whatever its platform
        // calls core protection -- FileVault, Gatekeeper, System Integrity
        // Protection -- and on Windows these two are that, which is why the fleet
        // Security report charts both. They were only on the Security tab, so the
        // overview said a machine was protected without mentioning the firmware.
        var sb = sec.SecureBoot;
        if (sb is not null)
            body.Children.Add(Ui.StatusBadge("Secure Boot",
                DeviceSnapshot.FirstNonEmpty(sb.StatusDisplay) ?? Format.EnabledDisabled(sb.IsEnabled),
                Ui.ToneFor(sb.IsEnabled)));

        var tpm = sec.Tpm;
        if (tpm is not null)
            // Present but switched off is the case worth seeing: the machine can do
            // this and is not, which reads differently from having no TPM at all.
            body.Children.Add(Ui.StatusBadge("TPM",
                DeviceSnapshot.FirstNonEmpty(tpm.StatusDisplay)
                    ?? (tpm.IsPresent ? Format.EnabledDisabled(tpm.IsEnabled) : "Not present"),
                tpm.IsPresent ? Ui.ToneFor(tpm.IsEnabled) : Tone.Error));

        return Ui.StatBlock("Security", "Windows protection status", "", Accent.Red, body);
    }
}
