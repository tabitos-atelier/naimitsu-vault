// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace NaimitsuVault.Services.Interfaces;

/// <summary>
/// Implemented by pages hosted in ShellWindow's NavFrame whose ViewModel tracks an active/inactive
/// lifecycle state (IsActive + Resume()/Pause()). ShellWindow.NavigateToTag calls Activated()/Deactivated()
/// directly at the point it swaps NavFrame.Content, because that assignment does not go through
/// Frame.Navigate() and therefore never raises Page.OnNavigatedTo/OnNavigatedFrom.
/// </summary>
public interface IPageActivationAware
{
    void Activated();
    void Deactivated();
}
