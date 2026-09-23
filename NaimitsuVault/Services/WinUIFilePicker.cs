// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Services.Interfaces;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace NaimitsuVault.Services;

public class WinUIFilePicker : IFilePickerService
{
    private nint _hwnd;

    public void SetHwnd(nint hwnd) => _hwnd = hwnd;

    private static SecureCharBuffer? PathToSecure(string? path)
    {
        if (path == null) return null;
        var buf = new SecureCharBuffer();
        buf.SetFromSpan(path.AsSpan());
        return buf;
    }

    public async Task<SecureCharBuffer?> OpenAsync(IReadOnlyList<(string description, string extension)> filters)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, _hwnd);
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        foreach (var (_, ext) in filters)
            picker.FileTypeFilter.Add(ext);
        if (picker.FileTypeFilter.Count == 0) picker.FileTypeFilter.Add("*");
        var file = await picker.PickSingleFileAsync();
        return PathToSecure(file?.Path);
    }

    public async Task<IReadOnlyList<SecureCharBuffer>> OpenMultipleAsync(IReadOnlyList<(string description, string extension)> filters)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, _hwnd);
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        foreach (var (_, ext) in filters)
            picker.FileTypeFilter.Add(ext);
        if (picker.FileTypeFilter.Count == 0) picker.FileTypeFilter.Add("*");
        var files = await picker.PickMultipleFilesAsync();
        if (files == null) return [];
        var result = new List<SecureCharBuffer>(files.Count);
        try
        {
            foreach (var f in files)
            {
                var buf = new SecureCharBuffer();
                buf.SetFromSpan(f.Path.AsSpan());
                result.Add(buf);
            }
        }
        catch
        {
            // Dispose every buffer already created in this call before the exception propagates,
            // so a mid-loop failure can't leave POH-pinned path buffers behind.
            foreach (var buf in result) buf.Dispose();
            throw;
        }
        return result;
    }

    public async Task<SecureCharBuffer?> SaveAsync(string defaultFileName,
        IReadOnlyList<(string description, string extension)> filters)
    {
        var picker = new FileSavePicker();
        InitializeWithWindow.Initialize(picker, _hwnd);
        picker.SuggestedFileName = defaultFileName;
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        foreach (var (desc, ext) in filters)
            picker.FileTypeChoices.Add(desc, [ext]);
        var file = await picker.PickSaveFileAsync();
        return PathToSecure(file?.Path);
    }

    public async Task<SecureCharBuffer?> OpenFolderAsync()
    {
        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, _hwnd);
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        return PathToSecure(folder?.Path);
    }
}
