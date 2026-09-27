using System.ComponentModel;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;
using Tandem.App.Services;
using Tandem.Core.Pairing;
using Windows.Storage.Streams;

namespace Tandem.App.Views.Setup;

/// <summary>
/// First-run guide: pick your phone brand, unlock Developer options, turn on Wireless
/// debugging, scan the code. Brand-specific wording and a drawn phone screen for each step.
/// Returning users (phone paired before, just not around) see a short "waiting" screen instead.
/// </summary>
public sealed partial class SetupGuideView : UserControl
{
    private enum Step { Brand, DeveloperOptions, WirelessDebugging, Pair }

    private Step _step = Step.Brand;
    private BrandGuide _brand = BrandGuide.Other;
    private bool _showWizard;
    private CancellationTokenSource? _pairingCts;

    public SetupGuideView()
    {
        InitializeComponent();
    }

    private static PhoneSession Session => AppServices.Current.Session;
    private static SettingsStore Settings => AppServices.Current.Settings;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Session.PropertyChanged += OnSessionChanged;
        _brand = BrandGuide.ById(Settings.Current.PhoneBrand);
        _showWizard = Settings.Current.LastPhoneName is null;
        _step = Step.Brand;
        Render();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Session.PropertyChanged -= OnSessionChanged;
        _pairingCts?.Cancel();
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PhoneSession.HasUnauthorizedDevice):
                UnauthorizedBar.IsOpen = Session.HasUnauthorizedDevice;
                break;
            case nameof(PhoneSession.AdbReady) when _step == Step.Pair && _showWizard:
                _ = StartQrAsync();
                break;
            case nameof(PhoneSession.IsConnected):
                // The Home page swaps this view out once connected; stop advertising the code.
                if (Session.IsConnected) _pairingCts?.Cancel();
                else Render();
                break;
        }
    }

    // ---------- rendering ----------

    private void Render()
    {
        UnauthorizedBar.IsOpen = Session.HasUnauthorizedDevice;
        var waiting = !_showWizard;
        WaitingPanel.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
        WizardPanel.Visibility = waiting ? Visibility.Collapsed : Visibility.Visible;
        if (waiting)
        {
            _pairingCts?.Cancel();
            RenderWaiting();
            return;
        }

        BrandGrid.Visibility = Visibility.Collapsed;
        StepList.Children.Clear();
        TipText.Children.Clear();
        TipBorder.Visibility = Visibility.Visible;
        PairExtras.Visibility = Visibility.Collapsed;
        QrPanel.Visibility = Visibility.Collapsed;
        Mock.Visibility = Visibility.Visible;
        BackButton.Visibility = _step == Step.Brand ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Visibility = Visibility.Visible;
        if (_step != Step.Pair) _pairingCts?.Cancel();

        switch (_step)
        {
            case Step.Brand: RenderBrand(); break;
            case Step.DeveloperOptions: RenderDeveloperOptions(); break;
            case Step.WirelessDebugging: RenderWirelessDebugging(); break;
            case Step.Pair: RenderPair(); break;
        }
    }

    private void RenderBrand()
    {
        StepLabel.Text = "Connect your phone";
        StepTitle.Text = "What phone do you have?";
        StepIntro.Text = "Tandem connects over Wi-Fi using Android's built-in Wireless debugging. Nothing to install on the phone. " +
                         "Pick your phone and we'll show you exactly where to tap. It takes about two minutes, once.";
        NextButton.Visibility = Visibility.Collapsed;
        Tip("Needs Android 11 or newer. You'll find your Android version in **Settings → About phone**.");

        BrandGrid.Visibility = Visibility.Visible;
        BrandGrid.Children.Clear();
        BrandGrid.RowDefinitions.Clear();
        BrandGrid.ColumnDefinitions.Clear();
        BrandGrid.ColumnDefinitions.Add(new ColumnDefinition());
        BrandGrid.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < BrandGuide.All.Count; i++)
        {
            if (i % 2 == 0) BrandGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var brand = BrandGuide.All[i];
            var content = new StackPanel { Spacing = 2 };
            content.Children.Add(new TextBlock { Text = brand.Name, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
            content.Children.Add(new TextBlock { Text = brand.Examples, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Foreground = Res<Brush>("TextFillColorSecondaryBrush") });
            var button = new Button
            {
                Content = content,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Stretch, // equal heights within a row
                VerticalContentAlignment = VerticalAlignment.Top,
                Padding = new Thickness(16, 12, 16, 12),
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, brand.Name);
            button.Click += (_, _) => ChooseBrand(brand);
            Grid.SetRow(button, i / 2);
            Grid.SetColumn(button, i % 2);
            BrandGrid.Children.Add(button);
        }

        Mock.Show("Settings", [
            new MockRow("Wi-Fi", MockEnd.Chevron),
            new MockRow("Bluetooth", MockEnd.Chevron),
            new MockRow("Display", MockEnd.Chevron),
            new MockRow("Battery", MockEnd.Chevron),
            new MockRow("About phone", MockEnd.Chevron),
        ]);
    }

    private void RenderDeveloperOptions()
    {
        StepLabel.Text = $"Step 1 of 3 · {_brand.Name}";
        StepTitle.Text = "Unlock Developer options";
        StepIntro.Text = "A normal Android setting for connecting to computers. It doesn't change anything else on your phone.";
        Numbered(
            $"On your phone, open **{_brand.AboutPath}**.",
            $"Tap **{_brand.TapTarget}** 7 times in a row. If your phone asks, enter your PIN.",
            "You'll see a message like **\"You are now a developer\"**. Done!");
        if (_brand.Note is not null) Tip(_brand.Note);
        Tip($"Already did this before? Just tap **Next**. Can't find it? Type **{_brand.TapTarget}** in the search bar at the top of Settings.");
        NextButton.Content = "Next";

        var rows = _brand.AboutRows.Select(r => r == _brand.TapTarget
            ? new MockRow(r, Highlight: true, Badge: "Tap 7 times", Detail: "…")
            : new MockRow(r, Detail: "…")).ToList();
        Mock.Show(_brand.AboutPath.Split('→').Last().Trim(), rows);
    }

    private void RenderWirelessDebugging()
    {
        StepLabel.Text = $"Step 2 of 3 · {_brand.Name}";
        StepTitle.Text = "Turn on Wireless debugging";
        StepIntro.Text = "This lets Tandem talk to your phone over your Wi-Fi. Only computers you pair can connect.";
        Numbered(
            $"Open **{_brand.DevOptionsPath}**.",
            "Scroll down and turn on **Wireless debugging**.",
            "If your phone asks **\"Allow wireless debugging on this network?\"**, tick **Always allow on this network** and tap **Allow**.");
        Tip("Can't find it? Type **Wireless debugging** in the search bar at the top of Settings.");
        NextButton.Content = "Next";
        Mock.Show("Developer options", [
            new MockRow("Stay awake", MockEnd.SwitchOff),
            new MockRow("USB debugging", MockEnd.SwitchOff),
            new MockRow("Wireless debugging", MockEnd.SwitchOn, Highlight: true, Badge: "Turn on"),
            new MockRow("Revoke USB debugging authorisations"),
        ]);
    }

    private void RenderPair()
    {
        StepLabel.Text = $"Step 3 of 3 · {_brand.Name}";
        StepTitle.Text = "Scan this code with your phone";
        StepIntro.Text = "Your phone and this PC need to be on the same Wi-Fi.";
        Numbered(
            "Tap the words **Wireless debugging** (not the switch) to open it.",
            "Tap **Pair device with QR code**.",
            "Point your phone at the code on the right. Tandem connects by itself.");
        Tip("Pairing is a one-time thing. Next time your phone is nearby, it just connects.");
        NextButton.Visibility = Visibility.Collapsed;
        PairExtras.Visibility = Visibility.Visible;
        Mock.Visibility = Visibility.Collapsed;
        QrPanel.Visibility = Visibility.Visible;
        if (Session.AdbReady) _ = StartQrAsync();
        else SetStatus("Starting…", busy: true);
    }

    private void RenderWaiting()
    {
        var name = Settings.Current.LastPhoneName ?? "your phone";
        WaitingTitle.Text = $"Looking for {name}…";
        WaitingChecks.Children.Clear();
        WaitingChecks.Children.Add(Check("", "Your phone is on the **same Wi-Fi** as this PC, and unlocked."));
        WaitingChecks.Children.Add(Check("",
            $"**Wireless debugging** is on. It switches off when your phone restarts. Turn it back on in **{_brand.DevOptionsPath} → Wireless debugging**."));
        WaitingChecks.Children.Add(Check("", "Or plug your phone into this PC with a USB cable."));
        WaitingMock.Show("Developer options", [
            new MockRow("USB debugging", MockEnd.SwitchOff),
            new MockRow("Wireless debugging", MockEnd.SwitchOn, Highlight: true, Badge: "Turn on"),
        ]);
    }

    // ---------- navigation ----------

    private void ChooseBrand(BrandGuide brand)
    {
        _brand = brand;
        Settings.Current.PhoneBrand = brand.Id;
        Settings.Save();
        _step = Step.DeveloperOptions;
        Render();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        _step = _step switch
        {
            Step.DeveloperOptions => Step.WirelessDebugging,
            Step.WirelessDebugging => Step.Pair,
            _ => _step,
        };
        Render();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        _step = _step switch
        {
            Step.Pair => Step.WirelessDebugging,
            Step.WirelessDebugging => Step.DeveloperOptions,
            _ => Step.Brand,
        };
        Render();
    }

    private void PairAgain_Click(object sender, RoutedEventArgs e)
    {
        _showWizard = true;
        _step = Step.Pair;
        Render();
    }

    private void NewPhone_Click(object sender, RoutedEventArgs e)
    {
        _showWizard = true;
        _step = Step.Brand;
        Render();
    }

    // ---------- pairing ----------

    private async Task StartQrAsync()
    {
        _pairingCts?.Cancel();
        var cts = _pairingCts = new CancellationTokenSource();
        var qr = QrPairing.Create();
        await ShowQrAsync(qr.QrPayload);
        var progress = new Progress<PairingStage>(stage => SetStatus(stage switch
        {
            PairingStage.WaitingForScan => "Waiting for your phone to scan…",
            PairingStage.Pairing => "Pairing…",
            PairingStage.Connecting => "Paired. Connecting…",
            _ => "Connected!",
        }, busy: stage != PairingStage.Connected, success: stage == PairingStage.Connected));
        try
        {
            await AppServices.Current.Pairing.PairWithQrAsync(qr, progress, cts.Token);
            await AppServices.Current.Tracker.RefreshAsync(CancellationToken.None);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested) SetStatus(ex.Message + " Try a new code.", busy: false, error: true);
        }
    }

    private async void CodePair_Click(object sender, RoutedEventArgs e)
    {
        _pairingCts?.Cancel();
        var cts = _pairingCts = new CancellationTokenSource();
        CodePairButton.IsEnabled = false;
        var progress = new Progress<PairingStage>(stage => SetStatus(stage switch
        {
            PairingStage.Pairing => "Pairing…",
            PairingStage.Connecting => "Paired. Connecting…",
            _ => "Connected!",
        }, busy: stage != PairingStage.Connected, success: stage == PairingStage.Connected));
        try
        {
            await AppServices.Current.Pairing.PairWithCodeAsync(EndpointBox.Text, CodeBox.Text, progress, cts.Token);
            await AppServices.Current.Tracker.RefreshAsync(CancellationToken.None);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SetStatus(ex.Message, busy: false, error: true);
        }
        finally
        {
            CodePairButton.IsEnabled = true;
        }
    }

    private void NewCode_Click(object sender, RoutedEventArgs e) => _ = StartQrAsync();

    private async Task ShowQrAsync(string payload)
    {
        QrLoading.IsActive = true;
        var png = await Task.Run(() =>
        {
            using var data = QRCodeGenerator.GenerateQrCode(payload, QRCodeGenerator.ECCLevel.M);
            return new PngByteQRCode(data).GetGraphic(12, [0x1a, 0x1a, 0x1a], [0xff, 0xff, 0xff], drawQuietZones: false);
        });
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(png.AsBuffer());
        stream.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        QrImage.Source = bitmap;
        QrLoading.IsActive = false;
    }

    private void SetStatus(string text, bool busy, bool success = false, bool error = false)
    {
        StatusText.Text = text;
        StatusRing.IsActive = busy;
        StatusRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StatusIcon.Visibility = success || error ? Visibility.Visible : Visibility.Collapsed;
        StatusIcon.Glyph = success ? "" : "";
        StatusIcon.Foreground = Res<Brush>(success ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush");
    }

    // ---------- helpers ----------

    private void Numbered(params string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var row = new Grid { ColumnSpacing = 14 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new Border
            {
                Style = Res<Style>("StepNumber"),
                Child = new TextBlock
                {
                    Text = (i + 1).ToString(System.Globalization.CultureInfo.CurrentCulture),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = Res<Brush>("TextOnAccentFillColorPrimaryBrush"),
                },
            });
            var text = RichText.Block(lines[i]);
            text.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            StepList.Children.Add(row);
        }
    }

    private void Tip(string markup) => TipText.Children.Add(RichText.Block(markup));

    private static UIElement Check(string glyph, string markup)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 18, VerticalAlignment = VerticalAlignment.Top, Foreground = Res<Brush>("AccentTextFillColorPrimaryBrush") });
        var text = RichText.Block(markup);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

    private static T Res<T>(string key) => (T)Application.Current.Resources[key];
}
