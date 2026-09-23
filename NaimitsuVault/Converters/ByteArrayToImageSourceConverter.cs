// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.IO;
using System.Security.Cryptography;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Extensions.Logging;
using NaimitsuVault.Helpers;

namespace NaimitsuVault.Converters;

/// <summary>
/// When used in a GridView/ListView ItemTemplate, always pass a thumbnail that has already been
/// resized (e.g. shrunk to a few hundred px or less via <see cref="Helpers.ImageHelper.CreateThumbnailAsync"/>).
/// Each Convert() call makes a defensive copy (<c>bytes.ToArray()</c>), so passing unresized original
/// data (up to <see cref="Common.AppConstants.MaxFileSizeMb"/>) directly causes a burst of copies during
/// rebinding on fast scrolling, putting pressure on the GC.
/// </summary>
public class ByteArrayToImageSourceConverter : IValueConverter
{
    private static readonly ILogger<ByteArrayToImageSourceConverter> Logger = AppLog.For<ByteArrayToImageSourceConverter>();

    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not byte[] bytes || bytes.Length == 0) return null;
        // AppSession._pinnedAvatar gets ZeroMemory'd on lock.
        // If ZeroMemory runs before the TryEnqueue lambda executes, it causes WINCODEC_ERR_BADIMAGE,
        // so make a defensive copy here and have the lambda reference only the copy.
        var snapshot = bytes.ToArray();
        var queue = App.UiDispatcherQueue;
        if (queue == null)
        {
            CryptographicOperations.ZeroMemory(snapshot);
            return null;
        }
        var image = new BitmapImage();
        var enqueued = queue.TryEnqueue(async () =>
        {
            try
            {
                using var ms = new MemoryStream(snapshot);
                await image.SetSourceAsync(ms.AsRandomAccessStream());
            }
            catch (Exception ex)
            {
                Logger.LogError("[ByteArrayToImageSourceConverter] An exception occurred while decoding the image stream. [{ExType}]", ex.GetType().Name);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(snapshot);
            }
        });
        if (!enqueued) CryptographicOperations.ZeroMemory(snapshot);
        return image;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
