// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.UI.Xaml;
using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.Services;

public class LockService : ILockService
{
    public void Lock()
    {
        if (Application.Current is App app)
            App.UiDispatcherQueue?.TryEnqueue(app.Lock);
    }

    public void LockAfterDataWrite()
    {
        if (Application.Current is App app)
            App.UiDispatcherQueue?.TryEnqueue(app.LockAfterDataWrite);
    }
}
