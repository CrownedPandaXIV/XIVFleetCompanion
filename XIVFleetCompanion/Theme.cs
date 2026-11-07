using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace XIVFleetCompanion
{
    // The looks' colours in the window (ImGui); the colours themselves are in ThemeLooks.cs.
    public static partial class Theme
    {
        /// <summary>The window's own colors for a look (none for Dalamud's), from its tokens.</summary>
        public static (ImGuiCol Col, Vector4 Color)[] Colors(ThemeChoice theme)
        {
            if (Info(theme).Tokens is not { } t) return System.Array.Empty<(ImGuiCol, Vector4)>();
            Vector4 C(string hex, float alpha = 1f) => Hex(hex, alpha);
            return new[]
            {
                (ImGuiCol.WindowBg, C(t.Bg, 0.97f)), (ImGuiCol.ChildBg, C(t.Bg, 0f)), (ImGuiCol.PopupBg, C(t.Modal, 0.98f)),
                (ImGuiCol.Border, C(t.Border)), (ImGuiCol.Separator, C(t.Border)),
                (ImGuiCol.Text, C(t.Text)), (ImGuiCol.TextDisabled, C(t.Dim)), (ImGuiCol.TextSelectedBg, C(t.Accent, 0.35f)),
                (ImGuiCol.TitleBg, C(t.Sidebar)), (ImGuiCol.TitleBgActive, C(t.Panel)), (ImGuiCol.TitleBgCollapsed, C(t.Sidebar)),
                (ImGuiCol.FrameBg, C(t.Panel)), (ImGuiCol.FrameBgHovered, C(t.Hover)), (ImGuiCol.FrameBgActive, C(t.Border)),
                (ImGuiCol.Button, C(t.Hover)), (ImGuiCol.ButtonHovered, C(t.HoverStrong)), (ImGuiCol.ButtonActive, C(t.Accent, 0.6f)),
                (ImGuiCol.Header, C(t.Accent, t.HeaderAlpha)), (ImGuiCol.HeaderHovered, C(t.Accent, t.HeaderAlpha >= 1 ? 0.55f : t.HeaderAlpha * 0.65f)),
                (ImGuiCol.HeaderActive, C(t.HeaderAlpha >= 1 ? t.AccentStrong : t.Accent, t.HeaderAlpha >= 1 ? 1f : 0.4f)),
                (ImGuiCol.CheckMark, C(t.Check)), (ImGuiCol.SliderGrab, C(t.Accent)), (ImGuiCol.SliderGrabActive, C(t.AccentStrong)),
                (ImGuiCol.Tab, C(t.Hover)), (ImGuiCol.TabHovered, C(t.Accent, 0.5f)),
                (ImGuiCol.TableHeaderBg, C(t.Panel)), (ImGuiCol.TableBorderStrong, C(t.Border)), (ImGuiCol.TableBorderLight, C(t.Hover)),
                (ImGuiCol.TableRowBg, C(t.Bg, 0f)), (ImGuiCol.TableRowBgAlt, C(t.RowAlt, t.RowAltAlpha)),
                (ImGuiCol.ScrollbarBg, C(t.Sidebar, 0.6f)), (ImGuiCol.ScrollbarGrab, C(t.Border)), (ImGuiCol.ScrollbarGrabHovered, C(t.BorderStrong)),
                (ImGuiCol.ResizeGrip, C(t.Accent, 0.2f)), (ImGuiCol.ResizeGripHovered, C(t.Accent, 0.5f)),
                (ImGuiCol.PlotLines, C(t.Accent)), (ImGuiCol.PlotHistogram, C(t.Accent)),
            };
        }

        /// <summary>Pushes a look's colors (before the window is drawn); returns how many, for <see cref="Pop"/>.</summary>
        public static int Push(ThemeChoice theme)
        {
            var colors = Colors(theme);
            foreach (var (col, color) in colors) ImGui.PushStyleColor(col, color);
            return colors.Length;
        }

        public static void Pop(int count)
        {
            if (count > 0) ImGui.PopStyleColor(count);
        }
    }
}
