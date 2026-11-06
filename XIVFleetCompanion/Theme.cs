using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace XIVFleetCompanion
{
    /// <summary>The look of the plugin's window (Settings → Look): only our window changes, never the game or other plugins.</summary>
    public enum ThemeChoice
    {
        /// <summary>The app's colors: dark blue-grey, with blue and amber accents.</summary>
        Fleet,
        /// <summary>Whatever Dalamud's style is, like other plugins.</summary>
        Dalamud,
        /// <summary>Black and white with strong borders, for reading at a glance.</summary>
        HighContrast,
    }

    /// <summary>The colors that carry meaning: fine or done, needs a look, urgent, and less important text.</summary>
    public sealed record StatusColors(Vector4 Good, Vector4 Warn, Vector4 Danger, Vector4 Dim);

    public static class Theme
    {
        private static Vector4 Hex(uint rgb, float alpha = 1f) =>
            new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, alpha);

        /// <summary>
        /// Status colors for a look. colourBlind: blue for fine, yellow for a look, vermilion for urgent (from the
        /// Okabe–Ito palette, told apart with every common kind of colour blindness) instead of green, amber and red.
        /// </summary>
        public static StatusColors Status(ThemeChoice theme, bool colourBlind)
        {
            var dim = theme == ThemeChoice.HighContrast ? Hex(0xC8C8C8) : Hex(0x8A8F9A);
            if (colourBlind) return new StatusColors(Hex(0x56B4E9), Hex(0xF0E442), Hex(0xFF7A3D), dim);
            return theme switch
            {
                ThemeChoice.HighContrast => new StatusColors(Hex(0x3DFF8A), Hex(0xFFD400), Hex(0xFF4D4D), dim),
                ThemeChoice.Dalamud => new StatusColors(new Vector4(0.55f, 0.85f, 0.55f, 1f), new Vector4(1f, 0.75f, 0.3f, 1f), new Vector4(1f, 0.45f, 0.45f, 1f), dim),
                _ => new StatusColors(Hex(0x6FBF82), Hex(0xE0B96A), Hex(0xE57373), dim),
            };
        }

        // The window's own colors for each look (Dalamud's look sets none).
        private static readonly Dictionary<ThemeChoice, (ImGuiCol Col, Vector4 Color)[]> Colors = new()
        {
            [ThemeChoice.Fleet] = new[]
            {
                (ImGuiCol.WindowBg, Hex(0x12141A, 0.97f)), (ImGuiCol.ChildBg, Hex(0x000000, 0f)), (ImGuiCol.PopupBg, Hex(0x1A1D24, 0.98f)),
                (ImGuiCol.Border, Hex(0x2A2E38)), (ImGuiCol.Separator, Hex(0x2A2E38)),
                (ImGuiCol.Text, Hex(0xE6E6E6)), (ImGuiCol.TextDisabled, Hex(0x8A8F9A)),
                (ImGuiCol.TitleBg, Hex(0x0D0F13)), (ImGuiCol.TitleBgActive, Hex(0x1A1D24)), (ImGuiCol.TitleBgCollapsed, Hex(0x0D0F13)),
                (ImGuiCol.FrameBg, Hex(0x1A1D24)), (ImGuiCol.FrameBgHovered, Hex(0x23262E)), (ImGuiCol.FrameBgActive, Hex(0x2A2E38)),
                (ImGuiCol.Button, Hex(0x23262E)), (ImGuiCol.ButtonHovered, Hex(0x2F3440)), (ImGuiCol.ButtonActive, Hex(0x5B8DEF, 0.6f)),
                (ImGuiCol.Header, Hex(0x5B8DEF, 0.28f)), (ImGuiCol.HeaderHovered, Hex(0x5B8DEF, 0.18f)), (ImGuiCol.HeaderActive, Hex(0x5B8DEF, 0.4f)),
                (ImGuiCol.CheckMark, Hex(0x5B8DEF)), (ImGuiCol.SliderGrab, Hex(0x5B8DEF)), (ImGuiCol.SliderGrabActive, Hex(0x3987E5)),
                (ImGuiCol.TableHeaderBg, Hex(0x1A1D24)), (ImGuiCol.TableBorderStrong, Hex(0x2A2E38)), (ImGuiCol.TableBorderLight, Hex(0x23262E)),
                (ImGuiCol.TableRowBg, Hex(0x000000, 0f)), (ImGuiCol.TableRowBgAlt, Hex(0xFFFFFF, 0.055f)),
                (ImGuiCol.ScrollbarBg, Hex(0x0D0F13, 0.6f)), (ImGuiCol.ScrollbarGrab, Hex(0x2A2E38)), (ImGuiCol.ScrollbarGrabHovered, Hex(0x3A3F4A)),
                (ImGuiCol.ResizeGrip, Hex(0x5B8DEF, 0.2f)), (ImGuiCol.ResizeGripHovered, Hex(0x5B8DEF, 0.5f)),
            },
            [ThemeChoice.HighContrast] = new[]
            {
                (ImGuiCol.WindowBg, Hex(0x000000)), (ImGuiCol.ChildBg, Hex(0x000000, 0f)), (ImGuiCol.PopupBg, Hex(0x000000)),
                (ImGuiCol.Border, Hex(0xFFFFFF, 0.7f)), (ImGuiCol.Separator, Hex(0xFFFFFF, 0.6f)),
                (ImGuiCol.Text, Hex(0xFFFFFF)), (ImGuiCol.TextDisabled, Hex(0xC8C8C8)),
                (ImGuiCol.TitleBg, Hex(0x000000)), (ImGuiCol.TitleBgActive, Hex(0x1E1E1E)), (ImGuiCol.TitleBgCollapsed, Hex(0x000000)),
                (ImGuiCol.FrameBg, Hex(0x1E1E1E)), (ImGuiCol.FrameBgHovered, Hex(0x333333)), (ImGuiCol.FrameBgActive, Hex(0x444444)),
                (ImGuiCol.Button, Hex(0x262626)), (ImGuiCol.ButtonHovered, Hex(0x3D3D3D)), (ImGuiCol.ButtonActive, Hex(0x0060DF)),
                (ImGuiCol.Header, Hex(0x0060DF)), (ImGuiCol.HeaderHovered, Hex(0x0060DF, 0.55f)), (ImGuiCol.HeaderActive, Hex(0x0050C0)),
                (ImGuiCol.CheckMark, Hex(0xFFFFFF)), (ImGuiCol.SliderGrab, Hex(0xFFFFFF)), (ImGuiCol.SliderGrabActive, Hex(0xC8C8C8)),
                (ImGuiCol.TableHeaderBg, Hex(0x1E1E1E)), (ImGuiCol.TableBorderStrong, Hex(0xFFFFFF, 0.7f)), (ImGuiCol.TableBorderLight, Hex(0xFFFFFF, 0.35f)),
                (ImGuiCol.TableRowBg, Hex(0x000000, 0f)), (ImGuiCol.TableRowBgAlt, Hex(0xFFFFFF, 0.10f)),
                (ImGuiCol.ScrollbarBg, Hex(0x000000)), (ImGuiCol.ScrollbarGrab, Hex(0x8C8C8C)), (ImGuiCol.ScrollbarGrabHovered, Hex(0xC8C8C8)),
                (ImGuiCol.ResizeGrip, Hex(0xFFFFFF, 0.3f)), (ImGuiCol.ResizeGripHovered, Hex(0xFFFFFF, 0.6f)),
            },
            [ThemeChoice.Dalamud] = System.Array.Empty<(ImGuiCol, Vector4)>(),
        };

        /// <summary>The sidebar's background for a look (null: the window's own).</summary>
        public static Vector4? SidebarBg(ThemeChoice theme) => theme switch
        {
            ThemeChoice.Fleet => Hex(0x0D0F13),
            ThemeChoice.HighContrast => Hex(0x0A0A0A),
            _ => null,
        };

        /// <summary>Pushes a look's colors (before the window is drawn); returns how many, for <see cref="Pop"/>.</summary>
        public static int Push(ThemeChoice theme)
        {
            var colors = Colors.TryGetValue(theme, out var c) ? c : System.Array.Empty<(ImGuiCol, Vector4)>();
            foreach (var (col, color) in colors) ImGui.PushStyleColor(col, color);
            return colors.Length;
        }

        public static void Pop(int count)
        {
            if (count > 0) ImGui.PopStyleColor(count);
        }
    }
}
