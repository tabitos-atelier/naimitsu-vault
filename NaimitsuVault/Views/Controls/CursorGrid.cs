// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace NaimitsuVault.Views.Controls;

/// <summary>A Grid that needs its cursor shape switched at runtime</summary>
public sealed class CursorGrid : Grid
{
    public InputCursor? ViewportCursor
    {
        get => ProtectedCursor;
        set => ProtectedCursor = value;
    }
}
