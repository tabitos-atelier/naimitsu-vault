// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NaimitsuVault.ViewModels;

public partial class AboutViewModel : ObservableObject
{
    public string Version { get; }

    public AboutViewModel()
    {
        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        Version = ver != null ? $"v{ver.Major}.{ver.Minor}.{ver.Build}" : "v1.0.0";
    }
}
