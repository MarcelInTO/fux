using Terminal.Gui.Drawing;

namespace Fux
{
    // Solarized, mapped the way the canonical vim-colors-solarized does it
    // (github.com/altercation/vim-colors-solarized), pinned as TrueColor RGB:
    //
    //   Normal        -> base0 on base03            (pane content)
    //   Visual        -> reversed base01            (selection bar; also lights the
    //                                                focused pane's border title, which
    //                                                Terminal.Gui v2 draws with Focus)
    //   StatusLine    -> base1 on base02            (menu + status chrome)
    //   WildMenu      -> reversed base2 on base02   (selected chrome item)
    //   Error         -> red                        Comment -> base01 italic
    //   String        -> cyan                       (value pane text)
    //   vim xml.vim:  elements blue (Function->Identifier), attributes + processing
    //                 instructions yellow (Type), CDATA cyan (String)
    //
    // Light mode is solarized.vim's own base flip — base03<->base3, base02<->base2,
    // base01<->base1, base00<->base0 — with identical accents, applied at runtime by
    // Load(): the same scheme definitions produce the light rendering automatically.
    internal static class Theme
    {
        // Fixed accents — identical in both modes.
        private static readonly Color Yellow = new Color("#b58900");
        private static readonly Color Red    = new Color("#dc322f");
        private static readonly Color Blue   = new Color("#268bd2");
        private static readonly Color Cyan   = new Color("#2aa198");
        private static readonly Color Orange = new Color("#cb4b16");

        // The sixteen-tone monotone ramp, named for dark mode.
        private static readonly Color S03 = new Color("#002b36");
        private static readonly Color S02 = new Color("#073642");
        private static readonly Color S01 = new Color("#586e75");
        private static readonly Color S00 = new Color("#657b83");
        private static readonly Color S0  = new Color("#839496");
        private static readonly Color S1  = new Color("#93a1a1");
        private static readonly Color S2  = new Color("#eee8d5");
        private static readonly Color S3  = new Color("#fdf6e3");

        // Mode-resolved base tones (dark: Base03 == S03; light: Base03 == S3, ...).
        private static Color Base03, Base02, Base01, Base00, Base0, Base1, Base2;

        public static bool IsDark { get; private set; }

        public static Scheme Content { get; private set; }
        public static Scheme Bar { get; private set; }
        public static Scheme Error { get; private set; }
        public static Scheme Flat { get; private set; }

        // Per-node-kind row schemes for the tree (vim xml.vim group links).
        public static Scheme NodeElement { get; private set; }      // Function -> blue
        public static Scheme NodeAttribute { get; private set; }    // Type -> yellow
        public static Scheme NodeComment { get; private set; }      // Comment -> base01 italic
        public static Scheme NodeCdata { get; private set; }        // String -> cyan

        // Per-severity row attributes for the error list.
        public static Attribute ErrorRow { get; private set; }      // Error -> red
        public static Attribute WarningRow { get; private set; }    // yellow (Error-red would hide the distinction)

        // Section headings in the snippet panel. vim's Title group, which solarized.vim maps
        // to orange — and orange is the one accent the tree does not already spend on a node
        // kind, so a heading cannot be misread as content.
        public static Attribute HeadingRow { get; private set; }    // Title -> orange

        static Theme() => Load(dark: true);

        public static void Load(bool dark)
        {
            IsDark = dark;
            Base03 = dark ? S03 : S3;
            Base02 = dark ? S02 : S2;
            Base01 = dark ? S01 : S1;
            Base00 = dark ? S00 : S0;
            Base0  = dark ? S0  : S00;
            Base1  = dark ? S1  : S01;
            Base2  = dark ? S2  : S02;

            Attribute A(Color fg, Color bg) => new Attribute(fg, bg);
            // vim Visual: reversed base01 — the selection bar in every pane.
            var visual = A(Base03, Base01);

            Content = new Scheme
            {
                Normal    = A(Base0,  Base03),
                Focus     = visual,
                HotNormal = A(Yellow, Base03),
                HotFocus  = A(Yellow, Base01),
                Disabled  = A(Base01, Base03),
            };
            // StatusLine chrome; WildMenu for the selected item.
            Bar = new Scheme
            {
                Normal    = A(Base1,  Base02),
                Focus     = A(Base02, Base2),
                HotNormal = A(Yellow, Base02),
                HotFocus  = A(Orange, Base2), // orange: yellow is unreadable on base2
                Disabled  = A(Base00, Base02),
            };
            Error = new Scheme
            {
                Normal    = A(Red,   Base03),
                Focus     = A(Base3Fixed(dark), Red),
                HotNormal = A(Red,   Base03),
                HotFocus  = A(Base3Fixed(dark), Red),
                Disabled  = A(Base01, Base03),
            };
            // Value pane: vim String -> cyan. Focus/ReadOnly pinned so the TextView neither
            // floods on focus nor gets v2's derived (off-palette) dimming.
            Flat = new Scheme
            {
                Normal    = A(Cyan,   Base03),
                Focus     = A(Cyan,   Base03),
                HotNormal = A(Yellow, Base03),
                HotFocus  = A(Yellow, Base03),
                Disabled  = A(Base01, Base03),
                ReadOnly  = A(Cyan,   Base03),
            };

            NodeElement   = Node(A(Blue,   Base03), visual);
            NodeAttribute = Node(A(Yellow, Base03), visual);
            NodeComment   = Node(new Attribute(Base01, Base03, TextStyle.Italic),
                                 new Attribute(Base03, Base01, TextStyle.Italic));
            NodeCdata     = Node(A(Cyan,   Base03), visual);

            ErrorRow   = A(Red,    Base03);
            WarningRow = A(Yellow, Base03);
            HeadingRow = A(Orange, Base03);
        }

        // A row scheme: the node-kind accent when unselected, the Visual bar when selected.
        private static Scheme Node(Attribute normal, Attribute focus) => new Scheme
        {
            Normal = normal, Focus = focus,
            HotNormal = normal, HotFocus = focus,
            Disabled = normal,
        };

        // Near-white for text on the red error bar: base3 in dark mode, base03 in light.
        private static Color Base3Fixed(bool dark) => dark ? S3 : S03;
    }
}
