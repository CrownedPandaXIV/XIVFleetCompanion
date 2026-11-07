using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace XIVFleetCompanion
{
    /// <summary>The look of the plugin's window (Settings → Look): only our window changes, never the game or other plugins.
    /// New looks go at the end, so a saved choice keeps its meaning.</summary>
    public enum ThemeChoice
    {
        /// <summary>The app's colors: dark blue-grey, with blue and amber accents.</summary>
        Fleet,
        /// <summary>Whatever Dalamud's style is, like other plugins.</summary>
        Dalamud,
        /// <summary>Black and white with strong borders, for reading at a glance.</summary>
        HighContrast,
        Princess,
        DeepSea,
        Sunset,
        Parchment,
        Evergreen,
    }

    /// <summary>
    /// The colors that carry meaning: fine or done, needs a look, urgent, and less important text; then the colors that
    /// only say what a thing is: gil (gold), a house's address (blue) and a Free Company's name (lavender). Those three
    /// are null (plain text) with colour blind status colors, so the only colors left there are the ones that mean
    /// something. FC points are aqua. Heading: headings' colour (null: the text's). The app uses the same colors
    /// (renderer/themes.js).
    /// </summary>
    public sealed record StatusColors(Vector4 Good, Vector4 Warn, Vector4 Danger, Vector4 Dim,
        Vector4? Gil = null, Vector4? Address = null, Vector4? Fc = null, Vector4? FcPoints = null, RoleColors? Roles = null, Vector4? Heading = null);

    /// <summary>
    /// A class's colour by its role, as the game groups them: tanks blue, healers green, damage red, each base class
    /// (Gladiator, Conjurer, ...) a lighter shade of its job's. Null with colour blind status colors (plain text).
    /// </summary>
    public sealed record RoleColors(Vector4 Tank, Vector4 TankBase, Vector4 Healer, Vector4 HealerBase, Vector4 Dps, Vector4 DpsBase);

    /// <summary>
    /// One look's colours, as the app's renderer/themes.js gives them (the plugin's tests check the two are the same):
    /// the window (Bg, Sidebar, Panel, Modal, Hover, HoverStrong, Border, BorderStrong, Text, TextBright, TextSoft, Dim,
    /// Faint, Accent, AccentStrong, Link, RowAlt and its alpha, Check, HeaderAlpha, Light, Heading, Glow), what things
    /// mean (Good, Warn, Danger), what they are (Gil, Address, Fc, FcPoints, Roles: tank, its base class, healer, base,
    /// damage, base) and the colour-blind friendly good, warn and danger (Cb).
    /// </summary>
    public sealed record ThemeTokens(string Bg, string Sidebar, string Panel, string Modal, string Hover, string HoverStrong,
        string Border, string BorderStrong, string Text, string TextBright, string TextSoft, string Dim, string Faint,
        string Accent, string AccentStrong, string Link, string RowAlt, float RowAltAlpha, string Check, float HeaderAlpha, bool Light,
        string? Heading, bool Glow, string Good, string Warn, string Danger, string Gil, string Address, string Fc, string FcPoints,
        string[] Roles, string[] Cb);

    /// <summary>A look: its key (the app's), name, what it's like, and its colours (null: Dalamud's own style).</summary>
    public sealed record ThemeInfo(ThemeChoice Choice, string Key, string Name, string About, ThemeTokens? Tokens);

    public static partial class Theme
    {
        public static Vector4 Hex(uint rgb, float alpha = 1f) =>
            new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, alpha);
        public static Vector4 Hex(string hex, float alpha = 1f) => Hex(uint.Parse(hex.TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture), alpha);

        private static readonly string[] StandardRoles = { "#5b8def", "#a3bff5", "#5fb870", "#a6d9af", "#e06464", "#efa6a6" };
        private static readonly string[] CbDark = { "#56b4e9", "#f0e442", "#ff7a3d" };

        /// <summary>Every look, in the order Settings lists them.</summary>
        public static readonly ThemeInfo[] Looks =
        {
            new(ThemeChoice.Fleet, "fleet", "XIV Fleet Companion", "The app's colors: dark blue-grey with blue and amber.", new(
                "#12141a", "#0d0f13", "#1a1d24", "#181b21", "#23262e", "#2f3440", "#2a2e38", "#3a3f4a", "#e6e6e6", "#ffffff", "#c0c4cc", "#8a8f9a",
                "#565b66", "#5b8def", "#3987e5", "#7aa7e0", "#ffffff", 0.055f, "#5b8def", 0.28f, false, null, false,
                "#6fbf82", "#e8a04c", "#e57373", "#f2d06b", "#8cc4f2", "#b8a6f0", "#5fd3c8", StandardRoles, CbDark)),
            new(ThemeChoice.Dalamud, "dalamud", "Dalamud", "Like your other plugins (Dalamud's own style).", null),
            new(ThemeChoice.HighContrast, "highContrast", "High contrast", "Black and white with strong borders.", new(
                "#000000", "#0a0a0a", "#1e1e1e", "#000000", "#333333", "#3d3d3d", "#b3b3b3", "#ffffff", "#ffffff", "#ffffff", "#e6e6e6", "#c8c8c8",
                "#8c8c8c", "#0060df", "#0050c0", "#7fd4ff", "#ffffff", 0.10f, "#ffffff", 1f, false, null, false,
                "#3dff8a", "#ff9f1a", "#ff4d4d", "#ffe066", "#7fd4ff", "#d0bcff", "#6ff0e2",
                new[] { "#6fa8ff", "#b5d2ff", "#5ce68a", "#adf2c4", "#ff6b6b", "#ffb3b3" }, CbDark)),
            new(ThemeChoice.Princess, "princess", "Princess", "Neon and pastel pink on deep plum. Very princessy.", new(
                "#1c0b1a", "#140713", "#2a1027", "#231021", "#3a1636", "#4d1d47", "#5c2152", "#8a3279", "#ffd6ec", "#ffffff", "#f5b8da", "#c79ab9",
                "#7d5170", "#ff4fb8", "#ff2da6", "#ff8fd0", "#ff4fb8", 0.06f, "#ff4fb8", 0.30f, false, "#ff8fd0", true,
                "#7ee8b0", "#ffb547", "#ff5f45", "#ffe27a", "#9fd4ff", "#d9b8ff", "#7df0e0",
                new[] { "#7fa8ff", "#b9ceff", "#6fdb96", "#b2eec7", "#ff6f6f", "#ffb0b0" }, new[] { "#6cc4f0", "#f5e85a", "#ff8a4c" })),
            new(ThemeChoice.DeepSea, "deepSea", "Deep Sea", "Dark navy and teal with glowing cyan, like the creatures down deep.", new(
                "#07141c", "#040d13", "#0c2230", "#0a1c28", "#113044", "#173d55", "#1a4258", "#2a5f7a", "#d8f1f5", "#ffffff", "#b2d6de", "#7fa3ae",
                "#456673", "#2ed3e0", "#1fb5c2", "#6fe3ec", "#2ed3e0", 0.05f, "#2ed3e0", 0.26f, false, null, false,
                "#5fe0a0", "#f2a65a", "#ff6b6b", "#f2d06b", "#9dbeff", "#c3a8f5", "#7ff0d4", StandardRoles, CbDark)),
            new(ThemeChoice.Sunset, "sunset", "Sunset Harbor", "Dusky purple with warm rose-gold, like evening at the docks.", new(
                "#1e1424", "#160e1b", "#2b1c33", "#25182c", "#3a2643", "#4a3155", "#4f3459", "#6e4a7a", "#f6e6da", "#ffffff", "#e0c9bd", "#a895a6",
                "#6a5670", "#e8917a", "#d9765c", "#f2b29f", "#e8917a", 0.06f, "#e8917a", 0.28f, false, null, false,
                "#7fd99a", "#ffb04a", "#ff5470", "#ffd866", "#9cc8f0", "#d1b3f5", "#6fd8cb", StandardRoles, CbDark)),
            new(ThemeChoice.Parchment, "parchment", "Parchment", "A light look: cream with dark brown text and ink blue, for daylight.", new(
                "#f4ecd8", "#eae0c8", "#fbf6ea", "#fbf6ea", "#e6dcc2", "#dccfb0", "#cbbe9e", "#a8996f", "#2b2118", "#120c06", "#4a3d2e", "#6b5e4c",
                "#a3967d", "#2e5a88", "#23476c", "#2e5a88", "#000000", 0.04f, "#2e5a88", 0.22f, true, null, false,
                "#2a6e2e", "#a35400", "#b3261e", "#7d5e00", "#1f5f99", "#6b4fa8", "#0c6e65",
                new[] { "#2f5fb3", "#45679f", "#245f2e", "#477550", "#b33a3a", "#a04e4e" }, new[] { "#005a9e", "#7a6400", "#b0306a" })),
            new(ThemeChoice.Evergreen, "evergreen", "Evergreen", "Dark forest green and bark brown with soft teal.", new(
                "#101a14", "#0a120d", "#18261d", "#15211a", "#213326", "#2a402f", "#2e4535", "#46624f", "#e3ede2", "#ffffff", "#c3d3c4", "#8fa294",
                "#55695a", "#4fa39a", "#3d8a82", "#7cc4bb", "#ffffff", 0.045f, "#6fc0b6", 0.30f, false, null, false,
                "#8be09f", "#f0a050", "#f07070", "#f2d06b", "#9cc8f0", "#c3aef0", "#6fd8cb", StandardRoles, CbDark)),
        };

        public static ThemeInfo Info(ThemeChoice theme) => Looks.FirstOrDefault(l => l.Choice == theme) ?? Looks[0];

        /// <summary>
        /// Status colors for a look. colourBlind: blue for fine, yellow for a look, orange-red for urgent (from the
        /// Okabe–Ito palette, shaded for each look's background so they stay readable and apart with every common kind
        /// of colour blindness) instead of green, amber and red; the colours that only say what a thing is are plain.
        /// </summary>
        public static StatusColors Status(ThemeChoice theme, bool colourBlind)
        {
            var t = Info(theme).Tokens;
            if (t == null)
            {
                // Dalamud's own style: the default look's colours, a little brighter where Dalamud's background is.
                var dim = Hex(0x8A8F9A);
                if (colourBlind) return new StatusColors(Hex(0x56B4E9), Hex(0xF0E442), Hex(0xFF7A3D), dim);
                return new StatusColors(new Vector4(0.55f, 0.85f, 0.55f, 1f), Hex(0xE8A04C), new Vector4(1f, 0.45f, 0.45f, 1f), dim,
                    Hex(0xF2D06B), Hex(0x8CC4F2), Hex(0xB8A6F0), Hex(0x5FD3C8), RolesOf(StandardRoles));
            }
            var heading = t.Heading != null ? Hex(t.Heading) : (Vector4?)null;
            if (colourBlind) return new StatusColors(Hex(t.Cb[0]), Hex(t.Cb[1]), Hex(t.Cb[2]), Hex(t.Dim), Heading: heading);
            return new StatusColors(Hex(t.Good), Hex(t.Warn), Hex(t.Danger), Hex(t.Dim), Hex(t.Gil), Hex(t.Address), Hex(t.Fc), Hex(t.FcPoints),
                RolesOf(t.Roles), heading);
        }

        private static RoleColors RolesOf(string[] r) => new(Hex(r[0]), Hex(r[1]), Hex(r[2]), Hex(r[3]), Hex(r[4]), Hex(r[5]));

        /// <summary>The sidebar's background for a look (null: the window's own).</summary>
        public static Vector4? SidebarBg(ThemeChoice theme) => Info(theme).Tokens is { } t ? Hex(t.Sidebar) : null;

        /// <summary>The colour the selected page glows in the sidebar (Princess), or null.</summary>
        public static Vector4? Glow(ThemeChoice theme) => Info(theme).Tokens is { Glow: true } t ? Hex(t.Accent) : null;

        /// <summary>A few of a look's colours, for its swatches in Settings.</summary>
        public static Vector4[] Swatches(ThemeChoice theme) => Info(theme).Tokens is { } t
            ? new[] { t.Bg, t.Panel, t.Accent, t.Text, t.Good, t.Warn, t.Danger, t.Gil }.Select(h => Hex(h)).ToArray()
            : new[] { new Vector4(0.12f, 0.12f, 0.14f, 1f), new Vector4(0.2f, 0.22f, 0.27f, 1f), new Vector4(0.26f, 0.59f, 0.98f, 1f), new Vector4(1, 1, 1, 1) };

    }
}
