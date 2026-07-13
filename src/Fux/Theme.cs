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

        public static ColorScheme Content { get; private set; }
        public static ColorScheme Bar { get; private set; }
        public static ColorScheme Error { get; private set; }

        // Must run after Application.Init() — Attribute.Make needs the initialized driver.
        public static void Apply()
        {
            Content = new ColorScheme
            {
                Normal    = A(Color.Gray,         Color.Black),     // base0 on base03
                Focus     = A(Color.White,        Color.DarkGray),  // selection: base1 on base02
                HotNormal = A(Color.BrightYellow, Color.Black),     // hotkeys
                HotFocus  = A(Color.BrightYellow, Color.DarkGray),
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

            // Apply globally so any view we don't touch explicitly still inherits the dark theme.
            Colors.TopLevel = Content;
            Colors.Base = Content;
            Colors.Menu = Bar;
            Colors.Dialog = Bar;
            Colors.Error = Error;
        }
    }
}
