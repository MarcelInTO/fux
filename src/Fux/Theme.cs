using Terminal.Gui.Drawing;

namespace Fux
{
    // Real Solarized Dark, pinned as TrueColor RGB (Terminal.Gui v2). Unlike the v1 theme —
    // which could only name 16 ANSI palette *slots* and so shifted appearance with every
    // terminal color scheme — these are absolute colors: the UI renders identically on any
    // TrueColor terminal, light or dark.
    internal static class Theme
    {
        // Solarized palette (Ethan Schoonover)
        private static readonly Color Base03 = new Color("#002b36"); // darkest background
        private static readonly Color Base02 = new Color("#073642"); // highlighted background
        private static readonly Color Base01 = new Color("#586e75"); // dim/secondary content
        private static readonly Color Base0  = new Color("#839496"); // body text
        private static readonly Color Base1  = new Color("#93a1a1"); // emphasized text
        private static readonly Color Base3  = new Color("#fdf6e3"); // near-white (selection text)
        private static readonly Color Yellow = new Color("#b58900");
        private static readonly Color Red    = new Color("#dc322f");
        private static readonly Color Blue   = new Color("#268bd2");
        private static readonly Color Cyan   = new Color("#2aa198");

        private static Attribute A(Color fg, Color bg) => new Attribute(fg, bg);

        // Content panes: body text on the darkest background; the selected row is a clearly
        // visible near-white-on-blue bar; hotkeys pick up the yellow accent.
        public static readonly Scheme Content = new Scheme
        {
            Normal    = A(Base0,  Base03),
            Focus     = A(Base3,  Blue),
            HotNormal = A(Yellow, Base03),
            HotFocus  = A(Yellow, Blue),
            Disabled  = A(Base01, Base03),
        };

        // Menu + status bar chrome: emphasized text on the highlight background; the selected
        // item is base03-on-cyan (cyan = "active" accent, distinct from the blue selection bar).
        public static readonly Scheme Bar = new Scheme
        {
            Normal    = A(Base1,  Base02),
            Focus     = A(Base03, Cyan),
            HotNormal = A(Yellow, Base02),
            HotFocus  = A(Base03, Cyan),
            Disabled  = A(Base01, Base02),
        };

        // Error dialogs / accents.
        public static readonly Scheme Error = new Scheme
        {
            Normal    = A(Red,    Base03),
            Focus     = A(Base3,  Red),
            HotNormal = A(Red,    Base03),
            HotFocus  = A(Base3,  Red),
            Disabled  = A(Base01, Base03),
        };

        // For views that paint their WHOLE area with Focus (e.g. TextView) rather than just a
        // selected row: keep the dark background on focus so the pane doesn't flood with the
        // selection color. Text brightens slightly instead. ReadOnly is pinned because v2
        // otherwise derives it by dimming Normal — off the Solarized palette.
        public static readonly Scheme Flat = new Scheme
        {
            Normal    = A(Base0,  Base03),
            Focus     = A(Base1,  Base03),
            HotNormal = A(Yellow, Base03),
            HotFocus  = A(Yellow, Base03),
            Disabled  = A(Base01, Base03),
            ReadOnly  = A(Base0,  Base03),
        };
    }
}
