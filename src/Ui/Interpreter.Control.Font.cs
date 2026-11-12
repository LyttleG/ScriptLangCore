namespace ScriptLangCore;

using DispatchVtbl; // OleEmbed, OleHostRegistry, OleEmbedRehost, ...

internal partial class Interpreter
{
    public abstract partial class Control
    {
        /// <summary>Font face name ("" = default GUI font). Set anytime; applied live via WM_SETFONT.</summary>
        public string FontName
        {
            get => _fontName;
            set { _fontName = value ?? ""; ApplyFont(); }
        }

        /// <summary>Font size in points (0 = engine default size for the DPI). Set anytime.</summary>
        public int FontSize
        {
            get => _fontSize;
            set { _fontSize = Math.Max(0, value); ApplyFont(); }
        }

        /// <summary>Bold text. Set anytime; applied live via WM_SETFONT.</summary>
        public bool FontBold
        {
            get => _fontBold;
            set { _fontBold = value; ApplyFont(); }
        }

        /// <summary>The font the engine last applied to this control. A control that paints itself needs it: a DC
        /// from BeginPaint carries the SYSTEM font, so text drawn without selecting this comes out at the system
        /// size however carefully FontSize was set.</summary>
        internal IntPtr CurrentFontHandle => _currentFont;

        internal void ApplyFont()
        {
            var dpi = handle != IntPtr.Zero ? (int)GetDpiForWindow(handle) : 96;
            if (_currentFont != IntPtr.Zero)
                DeleteObject(_currentFont);
            if (_fontName.Length > 0 || _fontSize > 0 || _fontBold)
            {
                const int defaultPts = 9;                 // when only the face name changes
                var pts = _fontSize > 0 ? _fontSize : defaultPts;
                var px = (int)Math.Round(pts * dpi / 72.0);
                _currentFont = CreateFont(
                    -px, 0, 0, 0, _fontBold ? 700 : 400, 0, 0, 0, 1, 0, 0, 0, 0,
                    _fontName.Length > 0 ? _fontName : "Segoe UI");
            }
            else
            {
                _currentFont = CreateDpiFont(dpi);
            }
            _lastFontDpi = dpi;
            if (handle != IntPtr.Zero)
            {
                // A RICHEDIT TAKES ITS FONT THE OTHER WAY, because WM_SETFONT RESETS ITS CHARACTER FORMATS: measured,
                // a box whose runs were coloured 0x3FA9F5 / 0xF0F0F0 / 0x4CAF50 came back 0x000000 after one
                // WM_SETFONT, i.e. black text on a dark client (reported as "the text colour turns black after the
                // window is dragged to the other screen", and this same call is what a FontName/FontSize assignment
                // sends). EM_SETCHARFORMAT names only the face, the size and the weight, so a script's colours live
                // through it.
                if (IsRichEditWindow())
                    ApplyRichEditFont();
                else
                    SendMessageW(handle, WM_SETFONT, (nuint)_currentFont, 1);
            }
        }

        /// <summary>True for a window created from a RichEdit class, which formats its text per run rather than
        /// through a window font. It asks the WINDOW CLASS rather than the wrapper type, because the class is what
        /// decides: a RichTextBox whose ladder fell back to EDIT must take the EDIT path, and a GenericControl made
        /// with "RICHEDIT50W" must take the RichEdit one.</summary>
        private bool IsRichEditWindow()
            => NativeClass.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase);

        /// <summary>The RichEdit half of <see cref="ApplyFont"/>: EM_SETCHARFORMAT(SCF_ALL) with CFM_FACE, CFM_SIZE
        /// and CFM_BOLD, so the whole text takes the font while every colour a script set stays exactly as it was.
        /// THE HEIGHT IS NOT SIMPLY POINTS TIMES 20, and the difference is measured: a RichEdit converts twips to
        /// pixels with the SCREEN's DPI (the system one) rather than the window's, so a plain `pts * 20` kept the
        /// 144 DPI pixel size on a 96 DPI monitor and the log read "very big" after a move (reported from a live
        /// session). So the font is converted THROUGH THE UNITS rather than through a remembered number: the pixel
        /// height is what CreateFont was given (`points * here / PointsPerInch`), and the twips are that height
        /// expressed in the units the CONTROL ITSELF reports, asked from its own DC (`GetDeviceCaps(LOGPIXELSY)`),
        /// which is the scale RichEdit will apply when it draws. Nothing here assumes 96, 144 or any other machine's
        /// metric: only the two unit definitions (72 points, 1440 twips per inch) are constants, and both are true by
        /// definition. The face name is 31 characters at most plus its terminator (LF_FACESIZE is 32 WCHARs).</summary>
        private void ApplyRichEditFont()
        {
            // The default is the size CreateDpiFont draws, expressed the same way, so a control with no FontSize of
            // its own matches what every other control gets instead of a rounded 8 pt.
            var face = _fontName.Length > 0 ? _fontName : "Segoe UI";
            var pixels = FontPixelHeight();   // the height CreateFont was given, from the ONE statement of it

            var ppi = 0;
            var dc = GetDC(handle);
            if (dc != IntPtr.Zero)
            {
                try { ppi = GetDeviceCaps(dc, LOGPIXELSY); }
                finally { ReleaseDC(handle, dc); }
            }
            if (ppi <= 0) ppi = (int)GetDpiForSystem();                       // no DC: the system's own answer
            if (ppi <= 0) ppi = 96;                                           // ...and 96 only as the last resort
            var twips = (int)Math.Round(pixels * (double)TwipsPerInch / ppi);  // the same height in RichEdit's units
            if (twips < 1) twips = 1;
            var cf = Marshal.AllocHGlobal(92);
            try
            {
                for (var i = 0; i < 92; i++) Marshal.WriteByte(cf, i, 0);
                Marshal.WriteInt32(cf, 0, 92);                                   // cbSize = sizeof(CHARFORMATW)
                Marshal.WriteInt32(cf, 4, unchecked((int)(CFM_FACE | CFM_SIZE | CFM_BOLD)));
                Marshal.WriteInt32(cf, 8, _fontBold ? CFE_BOLD : 0);              // dwEffects
                Marshal.WriteInt32(cf, 12, twips);                               // yHeight, in twips
                for (var i = 0; i < face.Length && i < 31; i++)
                    Marshal.WriteInt16(cf, 26 + i * 2, (short)face[i]);           // szFaceName
                SendMessageW(handle, EM_SETCHARFORMAT, SCF_ALL, cf);
            }
            finally
            {
                Marshal.FreeHGlobal(cf);
            }
        }


        /// <summary>The font in ONE call, through the very three properties a script can set one by one, so the two
        /// cannot disagree. A name left out keeps the engine&#39;s font and a size of 0 the engine&#39;s size; Application is
        /// what builds the handle and tells the window, exactly as it does for a property.</summary>
        public void SetFont(string? name = null, int size = 0, bool bold = false)
        {
            if (!string.IsNullOrEmpty(name)) { FontName = name; }
            if (size > 0) { FontSize = size; }
            FontBold = bold;
            ApplyFont();
        }

        /// <summary>The text's own height, in RichEdit's unit (TWIPS, 1/1440 inch). Reading it is how a script learns
        /// what the control actually got; assigning it states the height directly, which is the only way to make a
        /// dragged window keep its text size when the control's own point-to-twip conversion changes under it.</summary>
        public int CharHeight
        {
            get
            {
                if (handle == IntPtr.Zero) return 0;
                var cf = Marshal.AllocHGlobal(CharFormatSize);
                try
                {
                    for (var i = 0; i < CharFormatSize; i += 4) Marshal.WriteInt32(cf, i, 0);
                    Marshal.WriteInt32(cf, 0, CharFormatSize);
                    SendMessageW(handle, EM_GETCHARFORMAT, 0, cf);      // 0 = the control's DEFAULT format
                    return Marshal.ReadInt32(cf, CharFormatYHeight);
                }
                finally { Marshal.FreeHGlobal(cf); }
            }
            set
            {
                if (handle == IntPtr.Zero || value <= 0) return;
                var cf = Marshal.AllocHGlobal(CharFormatSize);
                try
                {
                    for (var i = 0; i < CharFormatSize; i += 4) Marshal.WriteInt32(cf, i, 0);
                    Marshal.WriteInt32(cf, 0, CharFormatSize);
                    Marshal.WriteInt32(cf, 4, CfmSize);                    // the height only: the face stays as it is
                    Marshal.WriteInt32(cf, CharFormatYHeight, value);
                    SendMessageW(handle, EM_SETCHARFORMAT, ScfAll, cf);
                }
                finally { Marshal.FreeHGlobal(cf); }
            }
        }

        /// <summary>The row height a paragraph asks for, in TWIPS. Assigning a value above zero states an EXACT row
        /// height, which is the one shape that keeps two text controls' rows equal whatever a line holds (measured: a
        /// line carrying a glyph the ASCII metrics do not cover draws 2 px taller, and a column of numbers beside it
        /// then drifts one row per couple of hundred lines). Zero goes back to the control's single spacing.</summary>
        public int LineSpacing
        {
            get
            {
                if (handle == IntPtr.Zero) return 0;
                var pf = Marshal.AllocHGlobal(ParaFormat2Size);
                try
                {
                    for (var i = 0; i < ParaFormat2Size; i += 4) Marshal.WriteInt32(pf, i, 0);
                    Marshal.WriteInt32(pf, 0, ParaFormat2Size);
                    SendMessageW(handle, EM_GETPARAFORMAT, 0, pf);
                    return Marshal.ReadInt32(pf, ParaFormatLineSpacing);
                }
                finally { Marshal.FreeHGlobal(pf); }
            }
            set
            {
                if (handle == IntPtr.Zero) return;
                // THE WHOLE TEXT, not the caret's paragraph: EM_SETPARAFORMAT applies to the SELECTION, so a script that
                // stated a row height with the caret somewhere in the middle got one paragraph changed (that is what the
                // clipboard tool had to select-all around, by hand). The caller's selection comes back afterwards.
                var range = SelectionRange();
                SendMessageW(handle, EM_SETSEL, 0, -1);
                var pf = Marshal.AllocHGlobal(ParaFormat2Size);
                try
                {
                    for (var i = 0; i < ParaFormat2Size; i += 4) Marshal.WriteInt32(pf, i, 0);
                    Marshal.WriteInt32(pf, 0, ParaFormat2Size);
                    Marshal.WriteInt32(pf, 4, unchecked((int)PfmLineSpacing));   // PFM_LINESPACING
                    Marshal.WriteInt32(pf, ParaFormatLineSpacing, Math.Max(0, value));
                    // rule 4 is EXACT (the value is a height in twips); rule 0 with a zero value is single spacing. THE
                    // RULE IS THE THIRD BYTE of the 4-byte group at 168 (sStyle is a SHORT, then bLineSpacingRule, then
                    // bOutlineLevel), so it is written shifted: writing a plain 4 there sets sStyle and leaves the rule
                    // at 0, which is the silent no-op that read back as "LineSpacing stayed 0" (measured).
                    Marshal.WriteInt32(pf, ParaFormatSpacingRule, value > 0 ? 4 << 16 : 0);
                    SendMessageW(handle, EM_SETPARAFORMAT, 0, pf);
                }
                finally
                {
                    Marshal.FreeHGlobal(pf);
                    SendMessageW(handle, EM_SETSEL, (nuint)range.Start, range.End);
                }
            }
        }

        /// <summary>The pixel height of the font this control draws with. <see cref="ApplyRichEditFont"/> asks it too,
        /// so the height a member reports and the height a RichEdit is given cannot drift.</summary>
        private int FontPixelHeight()
        {
            var dpi = handle != IntPtr.Zero ? (int)GetDpiForWindow(handle) : DesignDpi;
            var points = _fontSize > 0
                ? _fontSize
                : DefaultFontPixels * (double)PointsPerInch / DesignDpi;
            return (int)Math.Round(points * dpi / (double)PointsPerInch);
        }
    }
}
