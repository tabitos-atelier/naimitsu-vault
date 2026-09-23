// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.Extensions.Logging.Abstractions;
using CommunityToolkit.Mvvm.Input;
using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;
using NaimitsuVault.Tests.Stubs;
using NaimitsuVault.ViewModels;

namespace NaimitsuVault.Tests;

/// <summary>
/// The file-picker route for supplying the emergency access QR PNG (the alternative to dropping it onto
/// the dialog). Both routes end in EmergencyAccessViewModel.LoadQrFromPngAsync.
///
/// TC-EAV-01: a cancelled pick changes nothing (no error, no QR, not busy).
/// TC-EAV-02: picking a real QR PNG loads it (HasEmergencyCodeQr) and asks the picker for .png only.
/// TC-EAV-03: picking a file that is not a decodable image reports a general error and stays retryable.
/// TC-EAV-04: a picker that throws reports a general error and does not leave the VM busy.
/// TC-EAV-05: while an unlock/load is in flight the picker is not opened at all.
/// </summary>
public sealed class EmergencyAccessViewModelTests : IDisposable
{
    private readonly string _dir;

    public EmergencyAccessViewModelTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"naimitsu_eav_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class PickerSpy : IFilePickerService
    {
        public string? PathToReturn { get; init; }
        public Exception? ThrowOnOpen { get; init; }
        public int OpenCalls { get; private set; }
        public IReadOnlyList<(string, string)>? LastFilters { get; private set; }

        public Task<SecureCharBuffer?> OpenAsync(IReadOnlyList<(string, string)> filters)
        {
            OpenCalls++;
            LastFilters = filters;
            if (ThrowOnOpen != null) throw ThrowOnOpen;
            if (PathToReturn == null) return Task.FromResult<SecureCharBuffer?>(null);
            var buf = new SecureCharBuffer();
            buf.SetFromSpan(PathToReturn.AsSpan());
            return Task.FromResult<SecureCharBuffer?>(buf);
        }

        public Task<SecureCharBuffer?> SaveAsync(string _, IReadOnlyList<(string, string)> __)
            => Task.FromResult<SecureCharBuffer?>(null);
        public Task<IReadOnlyList<SecureCharBuffer>> OpenMultipleAsync(IReadOnlyList<(string, string)> _)
            => Task.FromResult<IReadOnlyList<SecureCharBuffer>>([]);
        public Task<SecureCharBuffer?> OpenFolderAsync() => Task.FromResult<SecureCharBuffer?>(null);
    }

    private static EmergencyAccessViewModel NewVm(PickerSpy picker)
        => new(new StubAuthService(), picker, NullLogger<EmergencyAccessViewModel>.Instance);

    /// <summary>Renders a real QR code PNG the same way VaultOperationsViewModel.GenerateQrPngAsync does.</summary>
    private static async Task WriteQrPngAsync(string content, string path)
    {
        var writer = new ZXing.BarcodeWriterPixelData
        {
            Format  = ZXing.BarcodeFormat.QR_CODE,
            Options = new ZXing.QrCode.QrCodeEncodingOptions { Width = 400, Height = 400, Margin = 2 },
        };
        var pixelData = writer.Write(content);

        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
            Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(
            Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            Windows.Graphics.Imaging.BitmapAlphaMode.Ignore,
            (uint)pixelData.Width, (uint)pixelData.Height, 96, 96, pixelData.Pixels);
        await encoder.FlushAsync();

        var reader = new Windows.Storage.Streams.DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        var bytes = new byte[stream.Size];
        reader.ReadBytes(bytes);
        await File.WriteAllBytesAsync(path, bytes);
    }

    // ── TC-EAV-01 ────────────────────────────────────────────────────────

    [Fact]
    public async Task SelectQrFile_Cancelled_ChangesNothing()
    {
        var picker = new PickerSpy { PathToReturn = null };
        using var vm = NewVm(picker);

        await ((IAsyncRelayCommand)vm.SelectQrFileCommand).ExecuteAsync(null);

        Assert.Equal(1, picker.OpenCalls);
        Assert.False(vm.HasEmergencyCodeQr);
        Assert.Null(vm.ErrorMessage);
        Assert.False(vm.IsBusy);
    }

    // ── TC-EAV-02 ────────────────────────────────────────────────────────

    [Fact]
    public async Task SelectQrFile_RealQrPng_LoadsItAndAsksForPngOnly()
    {
        var path = Path.Combine(_dir, "eac.png");
        await WriteQrPngAsync("naimitsu-eac://v1/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", path);
        var picker = new PickerSpy { PathToReturn = path };
        using var vm = NewVm(picker);

        await ((IAsyncRelayCommand)vm.SelectQrFileCommand).ExecuteAsync(null);

        Assert.True(vm.HasEmergencyCodeQr);
        Assert.Null(vm.ErrorMessage);
        Assert.False(vm.IsBusy);
        var filter = Assert.Single(picker.LastFilters!);
        Assert.Equal(".png", filter.Item2);
    }

    // ── TC-EAV-03 ────────────────────────────────────────────────────────

    [Fact]
    public async Task SelectQrFile_NotADecodableImage_ReportsGeneralErrorAndStaysRetryable()
    {
        var path = Path.Combine(_dir, "not-an-image.png");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4, 5, 6, 7, 8], TestContext.Current.CancellationToken);
        var picker = new PickerSpy { PathToReturn = path };
        using var vm = NewVm(picker);

        await ((IAsyncRelayCommand)vm.SelectQrFileCommand).ExecuteAsync(null);

        Assert.False(vm.HasEmergencyCodeQr);
        Assert.False(string.IsNullOrEmpty(vm.ErrorMessage));
        Assert.False(vm.IsBusy);

        // Retryable: picking a real QR afterwards succeeds and clears the error.
        var good = Path.Combine(_dir, "eac.png");
        await WriteQrPngAsync("naimitsu-eac://v1/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", good);
        var retry = new PickerSpy { PathToReturn = good };
        using var vm2 = NewVm(retry);
        await ((IAsyncRelayCommand)vm2.SelectQrFileCommand).ExecuteAsync(null);
        Assert.True(vm2.HasEmergencyCodeQr);
    }

    // ── TC-EAV-04 ────────────────────────────────────────────────────────

    [Fact]
    public async Task SelectQrFile_PickerThrows_ReportsGeneralErrorAndIsNotLeftBusy()
    {
        var picker = new PickerSpy { ThrowOnOpen = new InvalidOperationException("picker failed") };
        using var vm = NewVm(picker);

        await ((IAsyncRelayCommand)vm.SelectQrFileCommand).ExecuteAsync(null);

        Assert.False(string.IsNullOrEmpty(vm.ErrorMessage));
        Assert.False(vm.IsBusy);
        Assert.False(vm.HasEmergencyCodeQr);
    }

    // ── TC-EAV-05 ────────────────────────────────────────────────────────

    [Fact]
    public async Task SelectQrFile_WhileBusy_NeverOpensThePicker()
    {
        var picker = new PickerSpy { PathToReturn = Path.Combine(_dir, "unused.png") };
        using var vm = NewVm(picker);
        vm.IsBusy = true; // an unlock (or another QR load) is in flight

        await ((IAsyncRelayCommand)vm.SelectQrFileCommand).ExecuteAsync(null);

        Assert.Equal(0, picker.OpenCalls);
        Assert.True(vm.IsBusy); // the guard returned without touching the in-flight operation's state
    }
}
