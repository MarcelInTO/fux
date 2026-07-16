using Terminal.Gui;

namespace Fux
{
    // A muted, Solarized-Dark-flavored theme. Terminal.Gui v1 ColorSchemes are 16-color
    // (no TrueColor in Attribute), so these use the ANSI color slots: on a terminal whose
    // palette is Solarized Dark they render as the real Solarized tones; elsewhere they read
    // as a clean black/dark-gray theme. Either way, no bright default-blue backgrounds.
    //
    //   Black    -> base03 (background)      DarkGray -> base02 (highlights/bars)
    //   Gray     -> base0  (body text)       White    -> base1  (emphasis/selection)
    //   Cyan/Yellow/Red -> Solarized accents
    internal static class Theme
    {
        private static Attribute A(Color fg, Color bg) => Attribute.Make(fg, bg);
        // One attribute in every slot: for a FrameView border/title, which we recolor wholesale on
        // focus, so it shouldn't matter whether the frame draws with Normal or Focus.
        private static ColorScheme Mono(Attribute a)
            => new ColorScheme { Normal = a, Focus = a, HotNormal = a, HotFocus = a, Disabled = a };

        public static ColorScheme Content { get; private set; }
        public static ColorScheme Bar { get; private set; }
        public static ColorScheme Error { get; private set; }
        public static ColorScheme Flat { get; private set; }
        public static ColorScheme TitleOn { get; private set; }   // focused pane title strip
        public static ColorScheme TitleOff { get; private set; }  // unfocused pane title strip

        // Must run after Application.Init() — Attribute.Make needs the initialized driver.
        public static void Apply()
        {
            Content = new ColorScheme
            {
                Normal    = A(Color.Gray,         Color.Black),     // base0 on base03
                Focus     = A(Color.White,        Color.Blue),      // selection: white on the Solarized blue accent (a clearly visible bar)
                HotNormal = A(Color.BrightYellow, Color.Black),     // hotkeys
                HotFocus  = A(Color.BrightYellow, Color.Blue),
                Disabled  = A(Color.DarkGray,     Color.Black),     // base01 on base03
            };
            Bar = new ColorScheme
            {
                Normal    = A(Color.White,        Color.DarkGray),  // menu/status bar
                Focus     = A(Color.Black,        Color.Cyan),      // selected item: cyan accent
                HotNormal = A(Color.BrightYellow, Color.DarkGray),
                HotFocus  = A(Color.Black,        Color.Cyan),
                Disabled  = A(Color.Gray,         Color.DarkGray),
            };
            Error = new ColorScheme
            {
                Normal    = A(Color.BrightRed,    Color.Black),
                Focus     = A(Color.White,        Color.Red),
                HotNormal = A(Color.BrightRed,    Color.Black),
                HotFocus  = A(Color.White,        Color.Red),
                Disabled  = A(Color.DarkGray,     Color.Black),
            };
            // For views that paint their WHOLE area with Focus (e.g. TextView) rather than just a
            // selected row: keep the black background on focus so the pane doesn't flood with the
            // selection color. Text brightens slightly instead.
            Flat = new ColorScheme
            {
                Normal    = A(Color.Gray,         Color.Black),
                Focus     = A(Color.White,        Color.Black),
                HotNormal = A(Color.BrightYellow, Color.Black),
                HotFocus  = A(Color.BrightYellow, Color.Black),
                Disabled  = A(Color.DarkGray,     Color.Black),
            };
            // Pane title strips (tab-like): the active pane is black-on-cyan (an obvious highlighted
            // tab), inactive panes are white-on-dark-gray (a clearly readable strip — not the
            // near-invisible dark-on-dark of a plain dim foreground). Cyan (not the blue selection
            // bar) so "active pane" reads distinctly from "selected row".
            TitleOn  = Mono(A(Color.Black, Color.Cyan));
            TitleOff = Mono(A(Color.White, Color.DarkGray));

            // Apply globally so any view we don't touch explicitly still inherits the dark theme.
            Colors.TopLevel = Content;
            Colors.Base = Content;
            Colors.Menu = Bar;
            Colors.Dialog = Bar;
            Colors.Error = Error;
        }
    }
}
