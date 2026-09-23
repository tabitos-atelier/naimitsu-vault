// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace NaimitsuVault.Views.Controls;

/// <summary>A grip icon that indicates draggability via a hand cursor</summary>
public sealed class GripHandle : FontIcon
{
    public GripHandle()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
    }
}
