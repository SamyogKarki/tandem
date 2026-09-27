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

namespace Tandem.App.Views;

public sealed partial class PairingView : UserControl
{
    private CancellationTokenSource? _cts;

    public PairingView()
    {
        InitializeComponent();
    }

    public PhoneSession Session => AppServices.Current.Session;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Session.PropertyChanged += OnSessionChanged;
        if (Session.AdbReady) _ = StartQrAsync();
        else SetStatus("Starting…", busy: true);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Session.PropertyChanged -= OnSessionChanged;
        _cts?.Cancel();
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PhoneSession.AdbReady) && Session.AdbReady && _cts is null)
            _ = StartQrAsync();
        // Once a phone is connected the Home page swaps this view out; stop advertising the code.
        if (e.PropertyName == nameof(PhoneSession.IsConnected))
        {
            if (Session.IsConnected) _cts?.Cancel();
            else if (IsLoaded) _ = StartQrAsync();
        }
    }

    private async Task StartQrAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
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
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
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
        StatusIcon.Glyph = success ? "\uE73E" : "\uE783";
        StatusIcon.Foreground = (Brush)Application.Current.Resources[success
            ? "SystemFillColorSuccessBrush"
            : "SystemFillColorCriticalBrush"];
    }
}
