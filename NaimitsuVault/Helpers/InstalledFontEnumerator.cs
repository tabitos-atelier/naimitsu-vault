// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Runtime.InteropServices;

namespace NaimitsuVault.Helpers;

/// <summary>
/// Enumerates installed font family names via gdi32.dll EnumFontFamiliesEx.
/// No external NuGet dependency (System.Drawing.Common etc.) is used.
/// </summary>
public static class InstalledFontEnumerator
{
    private const byte DEFAULT_CHARSET = 1;

    public static string[] GetInstalledFontFamilies()
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        nint hdc = GetDC(nint.Zero);
        if (hdc == nint.Zero) return [];
        try
        {
            var logFont = new LOGFONT { lfCharSet = DEFAULT_CHARSET, lfFaceName = string.Empty };
            EnumFontFamiliesEx(hdc, ref logFont, (ref LOGFONT lpelfe, nint lpntme, uint fontType, nint lParam) =>
            {
                var name = lpelfe.lfFaceName;
                // Names starting with '@' are the vertical-writing variant of a CJK font; skip them.
                if (!string.IsNullOrEmpty(name) && name[0] != '@')
                    names.Add(name);
                return 1;
            }, nint.Zero, 0);
        }
        finally
        {
            ReleaseDC(nint.Zero, hdc);
        }
        return names.ToArray();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LOGFONT
    {
        public int lfHeight;
        public int lfWidth;
        public int lfEscapement;
        public int lfOrientation;
        public int lfWeight;
        public byte lfItalic;
        public byte lfUnderline;
        public byte lfStrikeOut;
        public byte lfCharSet;
        public byte lfOutPrecision;
        public byte lfClipPrecision;
        public byte lfQuality;
        public byte lfPitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string lfFaceName;
    }

    private delegate int EnumFontFamExProc(ref LOGFONT lpelfe, nint lpntme, uint fontType, nint lParam);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int EnumFontFamiliesEx(nint hdc, ref LOGFONT lpLogfont, EnumFontFamExProc lpProc, nint lParam, uint dwFlags);

    [DllImport("user32.dll")] private static extern nint GetDC(nint hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hWnd, nint hDC);
}
