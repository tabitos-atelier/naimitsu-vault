// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace NaimitsuVault.Services.Interfaces;

public enum NotificationSeverity { Success, Info, Warning, Error }

public interface IAppNotificationService
{
    /// <summary>
    /// Use when both the title and message can be specified as locale keys (int).
    /// The closure captures only plain ints and holds no string references at all.
    /// String resolution via LocalizationManager.GetById() is deferred until the lambda executes (on the UI thread).
    /// </summary>
    void Show(int titleKey, int messageKey,
              NotificationSeverity severity = NotificationSeverity.Info,
              TimeSpan? duration = null);

    /// <summary>
    /// Use when the message is built dynamically at runtime (a string.Format result, a variable, etc.).
    /// The title is resolved lazily as an int code.
    /// </summary>
    void Show(int titleKey, string message,
              NotificationSeverity severity = NotificationSeverity.Info,
              TimeSpan? duration = null);
}
